﻿using System;
using System.IO;
using System.Linq;
using System.Messaging;
using System.Transactions;
using Microsoft.Practices.Unity;
using Rhino.Queues;
using Rhino.ServiceBus.Actions;
using Rhino.ServiceBus.Config;
using Rhino.ServiceBus.Convertors;
using Rhino.ServiceBus.DataStructures;
using Rhino.ServiceBus.Impl;
using Rhino.ServiceBus.Internal;
using Rhino.ServiceBus.LoadBalancer;
using Rhino.ServiceBus.MessageModules;
using Rhino.ServiceBus.Msmq;
using Rhino.ServiceBus.Msmq.TransportActions;
using Rhino.ServiceBus.RhinoQueues;
using ErrorAction = Rhino.ServiceBus.Msmq.TransportActions.ErrorAction;
using IStartable = Rhino.ServiceBus.Internal.IStartable;
using LoadBalancerConfiguration = Rhino.ServiceBus.LoadBalancer.LoadBalancerConfiguration;

namespace Rhino.ServiceBus.Unity
{
    public class UnityBuilder : IBusContainerBuilder
    {
        private readonly IUnityContainer container;
        private readonly AbstractRhinoServiceBusConfiguration config;

        public UnityBuilder(IUnityContainer container, AbstractRhinoServiceBusConfiguration config)
        {
            this.container = container;
            this.config = config;
            this.config.BuildWith(this);
        }

        public void WithInterceptor(IConsumerInterceptor interceptor)
        {
            container.AddExtension(new ConsumerExtension(interceptor));
        }

        public void RegisterDefaultServices()
        {
            if (!container.IsRegistered(typeof(IUnityContainer)))
                container.RegisterInstance(container);

            container.RegisterType<IServiceLocator, UnityServiceLocator>();
            container.RegisterTypesFromAssembly<IBusConfigurationAware>(typeof(IServiceBus).Assembly);

            foreach (var configurationAware in container.ResolveAll<IBusConfigurationAware>())
            {
                configurationAware.Configure(config, this);
            }

            foreach (var type in config.MessageModules)
            {
                if (!container.IsRegistered(type))
                    container.RegisterType(type, type.FullName);
            }

            container.RegisterType<IReflection, DefaultReflection>(new ContainerControlledLifetimeManager());
            container.RegisterType(typeof (IMessageSerializer), config.SerializerType, new ContainerControlledLifetimeManager());
            container.RegisterType<IEndpointRouter, EndpointRouter>(new ContainerControlledLifetimeManager());
        }

        public void RegisterBus()
        {
            var busConfig = (RhinoServiceBusConfiguration)config;

            container.RegisterType<IDeploymentAction, CreateLogQueueAction>(Guid.NewGuid().ToString())
                .RegisterType<IDeploymentAction, CreateQueuesAction>(Guid.NewGuid().ToString());

            container.RegisterType<DefaultServiceBus>(new ContainerControlledLifetimeManager())
                .RegisterType<IStartableServiceBus, DefaultServiceBus>(
                    new InjectionConstructor(
                        new ResolvedParameter<IServiceLocator>(),
                        new ResolvedParameter<ITransport>(),
                        new ResolvedParameter<ISubscriptionStorage>(),
                        new ResolvedParameter<IReflection>(),
                        new ResolvedArrayParameter<IMessageModule>(),
                        new InjectionParameter<MessageOwner[]>(busConfig.MessageOwners.ToArray()),
                        new ResolvedParameter<IEndpointRouter>()))
                .RegisterType<IServiceBus, DefaultServiceBus>()
                .RegisterType<IStartable, DefaultServiceBus>();
        }

        public void RegisterPrimaryLoadBalancer()
        {
            var loadBalancerConfig = (LoadBalancerConfiguration) config;
            container.RegisterType<MsmqLoadBalancer>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IMessageSerializer>(),
                    new ResolvedParameter<IQueueStrategy>(),
                    new ResolvedParameter<IEndpointRouter>(),
                    new InjectionParameter<Uri>(loadBalancerConfig.Endpoint),
                    new InjectionParameter<int>(loadBalancerConfig.ThreadCount),
                    new InjectionParameter<Uri>(loadBalancerConfig.SecondaryLoadBalancer),
                    new InjectionParameter<TransactionalOptions>(loadBalancerConfig.Transactional),
                    new ResolvedParameter<IMessageBuilder<Message>>()))
                .RegisterType<IStartable, MsmqLoadBalancer>(new ContainerControlledLifetimeManager());

            container.RegisterType<IDeploymentAction, CreateLoadBalancerQueuesAction>(Guid.NewGuid().ToString());
        }

        public void RegisterSecondaryLoadBalancer()
        {
            var loadBalancerConfig = (LoadBalancerConfiguration)config;
            container.RegisterType<MsmqSecondaryLoadBalancer>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IMessageSerializer>(),
                    new ResolvedParameter<IQueueStrategy>(),
                    new ResolvedParameter<IEndpointRouter>(),
                    new InjectionParameter<Uri>(loadBalancerConfig.Endpoint),
                    new InjectionParameter<Uri>(loadBalancerConfig.PrimaryLoadBalancer),
                    new InjectionParameter<int>(loadBalancerConfig.ThreadCount),
                    new InjectionParameter<TransactionalOptions>(loadBalancerConfig.Transactional),
                    new ResolvedParameter<IMessageBuilder<Message>>()))
                .RegisterType<IStartable, MsmqSecondaryLoadBalancer>(new ContainerControlledLifetimeManager());

            container.RegisterType<IDeploymentAction, CreateLoadBalancerQueuesAction>(Guid.NewGuid().ToString());
        }

        public void RegisterReadyForWork()
        {
            var loadBalancerConfig = (LoadBalancerConfiguration)config;
            container.RegisterType<MsmqReadyForWorkListener>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IQueueStrategy>(),
                    new InjectionParameter<Uri>(loadBalancerConfig.ReadyForWork),
                    new InjectionParameter<int>(loadBalancerConfig.ThreadCount),
                    new ResolvedParameter<IMessageSerializer>(),
                    new ResolvedParameter<IEndpointRouter>(),
                    new InjectionParameter<TransactionalOptions>(loadBalancerConfig.Transactional),
                    new ResolvedParameter<IMessageBuilder<Message>>()));

            container.RegisterType<IDeploymentAction, CreateReadyForWorkQueuesAction>(Guid.NewGuid().ToString());
        }

        public void RegisterLoadBalancerEndpoint(Uri loadBalancerEndpoint)
        {
            container.RegisterType<LoadBalancerMessageModule>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<Uri>(loadBalancerEndpoint),
                    new ResolvedParameter<IEndpointRouter>()));
        }

        public void RegisterLoggingEndpoint(Uri logEndpoint)
        {
            container.RegisterType<MessageLoggingModule>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IEndpointRouter>(),
                    new InjectionParameter<Uri>(logEndpoint)));
        }

        public void RegisterMsmqTransport(Type queueStrategyType)
        {
            if (queueStrategyType.Equals(typeof(FlatQueueStrategy)))
            {
                container.RegisterType(typeof (IQueueStrategy), queueStrategyType,
                                       new ContainerControlledLifetimeManager(),
                                       new InjectionConstructor(
                                           new ResolvedParameter<IEndpointRouter>(),
                                           new InjectionParameter<Uri>(config.Endpoint)));
            }
            else
            {
                container.RegisterType(typeof (IQueueStrategy), queueStrategyType);
            }

            container.RegisterType<IMessageBuilder<Message>, MsmqMessageBuilder>(
                new ContainerControlledLifetimeManager());
            container.RegisterType<IMsmqTransportAction, ErrorAction>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<int>(config.NumberOfRetries),
                    new ResolvedParameter<IQueueStrategy>()));
            container.RegisterType<ISubscriptionStorage, MsmqSubscriptionStorage>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IReflection>(),
                    new ResolvedParameter<IMessageSerializer>(),
                    new InjectionParameter<Uri>(config.Endpoint),
                    new ResolvedParameter<IEndpointRouter>(),
                    new ResolvedParameter<IQueueStrategy>()));
            container.RegisterType<ITransport, MsmqTransport>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IMessageSerializer>(),
                    new ResolvedParameter<IQueueStrategy>(),
                    new InjectionParameter<Uri>(config.Endpoint),
                    new InjectionParameter<int>(config.ThreadCount),
                    new ResolvedArrayParameter<IMsmqTransportAction>(),
                    new ResolvedParameter<IEndpointRouter>(),
                    new InjectionParameter<IsolationLevel>(config.IsolationLevel),
                    new InjectionParameter<TransactionalOptions>(config.Transactional),
                    new InjectionParameter<bool>(config.ConsumeInTransaction),
                    new ResolvedParameter<IMessageBuilder<Message>>()));

            container.RegisterTypesFromAssembly<IMsmqTransportAction>(typeof(IMsmqTransportAction).Assembly, typeof(ErrorAction));
        }

        public void RegisterQueueCreation()
        {
            container.RegisterType<QueueCreationModule>(new ContainerControlledLifetimeManager());
        }

        public void RegisterMsmqOneWay()
        {
            var oneWayConfig = (OnewayRhinoServiceBusConfiguration)config;
            container.RegisterType<IMessageBuilder<Message>, MsmqMessageBuilder>(
                new ContainerControlledLifetimeManager());
            container.RegisterType<IOnewayBus, MsmqOnewayBus>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<MessageOwner[]>(oneWayConfig.MessageOwners),
                    new ResolvedParameter<IMessageBuilder<Message>>()
                    ));
        }

        public void RegisterRhinoQueuesTransport(string path, string name)
        {
            container.RegisterType<ISubscriptionStorage, PhtSubscriptionStorage>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<string>(Path.Combine(path, name + "_subscriptions.esent")),
                    new ResolvedParameter<IMessageSerializer>(),
                    new ResolvedParameter<IReflection>()));

            container.RegisterType<ITransport, RhinoQueuesTransport>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<Uri>(config.Endpoint),
                    new ResolvedParameter<IEndpointRouter>(),
                    new ResolvedParameter<IMessageSerializer>(),
                    new InjectionParameter<int>(config.ThreadCount),
                    new InjectionParameter<string>(Path.Combine(path, name + ".esent")),
                    new InjectionParameter<IsolationLevel>(config.IsolationLevel),
                    new InjectionParameter<int>(config.NumberOfRetries),
                    new ResolvedParameter<IMessageBuilder<MessagePayload>>()));

            container.RegisterType<IMessageBuilder<MessagePayload>, RhinoQueuesMessageBuilder>(
                new ContainerControlledLifetimeManager());
        }

        public void RegisterRhinoQueuesOneWay()
        {
            var oneWayConfig = (OnewayRhinoServiceBusConfiguration)config;
            container.RegisterType<IMessageBuilder<MessagePayload>, RhinoQueuesMessageBuilder>(
                new ContainerControlledLifetimeManager());
            container.RegisterType<IOnewayBus, RhinoQueuesOneWayBus>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<MessageOwner[]>(oneWayConfig.MessageOwners),
                    new ResolvedParameter<IMessageSerializer>(),
                    new ResolvedParameter<IMessageBuilder<MessagePayload>>()));
        }

        public void RegisterSecurity(byte[] key)
        {
            container.RegisterType<IEncryptionService, RijndaelEncryptionService>("esb.security",
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new InjectionParameter<byte[]>(key)));

            container.RegisterType<IValueConvertor<WireEcryptedString>, WireEcryptedStringConvertor>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IEncryptionService>("esb.security")));
            container.RegisterType<IElementSerializationBehavior, WireEncryptedMessageConvertor>(
                new ContainerControlledLifetimeManager(),
                new InjectionConstructor(
                    new ResolvedParameter<IEncryptionService>("esb.security")));
        }

        public void RegisterNoSecurity()
        {
            container.RegisterType<IValueConvertor<WireEcryptedString>, ThrowingWireEcryptedStringConvertor>(
                new ContainerControlledLifetimeManager());
            container.RegisterType<IElementSerializationBehavior, ThrowingWireEncryptedMessageConvertor>(
                new ContainerControlledLifetimeManager());
        }
    }
}