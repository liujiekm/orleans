﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Storage;
using Orleans.MultiCluster;
using Orleans.Hosting;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.TestHooks;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Storage;

namespace Orleans.TestingHost
{
    /// <summary>Allows programmatically hosting an Orleans silo in the curent app domain, exposing some marshable members via remoting.</summary>
    public class AppDomainSiloHost : MarshalByRefObject
    {
        private readonly ISiloHost host;

        /// <summary>Creates and initializes a silo in the current app domain.</summary>
        /// <param name="name">Name of this silo.</param>
        /// <param name="siloBuilderFactoryType">Type of silo host builder factory.</param>
        /// <param name="config">Silo config data to be used for this silo.</param>
        public AppDomainSiloHost(string name, Type siloBuilderFactoryType, ClusterConfiguration config)
        {
            var builderFactory = (ISiloBuilderFactory)Activator.CreateInstance(siloBuilderFactoryType);
            ISiloHostBuilder builder = builderFactory
                .CreateSiloBuilder(name, config)
                .ConfigureServices(services => services.AddSingleton<TestHooksSystemTarget>())
                .AddApplicationPartsFromAppDomain()
                .AddApplicationPartsFromBasePath();
            this.host = builder.Build();
            InitializeTestHooksSystemTarget();
            this.AppDomainTestHook = new AppDomainTestHooks(this.host);
        }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => this.host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        
        internal AppDomainTestHooks AppDomainTestHook { get; }
        
        /// <summary>Starts the silo</summary>
        public void Start()
        {
            this.host.StartAsync().GetAwaiter().GetResult();
        }

        /// <summary>Gracefully shuts down the silo</summary>
        public void Shutdown()
        {
            this.host.StopAsync().GetAwaiter().GetResult();
        }

        private void InitializeTestHooksSystemTarget()
        {
            var testHook = this.host.Services.GetRequiredService<TestHooksSystemTarget>();
            var providerRuntime = this.host.Services.GetRequiredService<SiloProviderRuntime>();
            providerRuntime.RegisterSystemTarget(testHook);
        }
    }

    /// <summary>
    /// Test hook functions for white box testing.
    /// NOTE: this class has to and will be removed entirely. This requires the tests that currently rely on it, to assert using different mechanisms, such as with grains.
    /// </summary>
    internal class AppDomainTestHooks : MarshalByRefObject
    {
        private readonly ISiloHost host;

        public AppDomainTestHooks(ISiloHost host)
        {
            this.host = host;
        }

        internal IBootstrapProvider GetBootstrapProvider(string name)
        {
            var bootstrapProviderManager = this.host.Services.GetRequiredService<BootstrapProviderManager>();
            IBootstrapProvider provider = (IBootstrapProvider)bootstrapProviderManager.GetProvider(name);
            return CheckReturnBoundaryReference("bootstrap provider", provider);
        }

        /// <summary>Find the named storage provider loaded in this silo. </summary>
        internal IStorageProvider GetStorageProvider(string name) => CheckReturnBoundaryReference("storage provider", (IStorageProvider)this.host.Services.GetRequiredService<StorageProviderManager>().GetProvider(name));

        private static T CheckReturnBoundaryReference<T>(string what, T obj) where T : class
        {
            if (obj == null) return null;
            if (obj is MarshalByRefObject || obj is ISerializable)
            {
                // Reference to the provider can safely be passed across app-domain boundary in unit test process
                return obj;
            }
            throw new InvalidOperationException(
                $"Cannot return reference to {what} {TypeUtils.GetFullName(obj.GetType())} if it is not MarshalByRefObject or Serializable");
        }

        public IDictionary<GrainId, IGrainInfo> GetDirectoryForTypeNamesContaining(string expr)
        {
            var x = new Dictionary<GrainId, IGrainInfo>();
            LocalGrainDirectory localGrainDirectory = this.host.Services.GetRequiredService<LocalGrainDirectory>();
            var catalog = this.host.Services.GetRequiredService<Catalog>();
            foreach (var kvp in localGrainDirectory.DirectoryPartition.GetItems())
            {
                if (kvp.Key.IsSystemTarget || kvp.Key.IsClient || !kvp.Key.IsGrain)
                    continue;// Skip system grains, system targets and clients
                if (catalog.GetGrainTypeName(kvp.Key).Contains(expr))
                    x.Add(kvp.Key, kvp.Value);
            }
            return x;
        }
        
        // store silos for which we simulate faulty communication
        // number indicates how many percent of requests are lost
        private ConcurrentDictionary<IPEndPoint, double> simulatedMessageLoss;
        private readonly SafeRandom random = new SafeRandom();

        internal void BlockSiloCommunication(IPEndPoint destination, double lossPercentage)
        {
            if (simulatedMessageLoss == null)
                simulatedMessageLoss = new ConcurrentDictionary<IPEndPoint, double>();

            simulatedMessageLoss[destination] = lossPercentage;

            var mc = this.host.Services.GetRequiredService<MessageCenter>();
            mc.ShouldDrop = ShouldDrop;
        }

        internal void UnblockSiloCommunication()
        {
            var mc = this.host.Services.GetRequiredService<MessageCenter>();
            mc.ShouldDrop = null;
            simulatedMessageLoss.Clear();
        }

        internal Func<ILogConsistencyProtocolMessage,bool> ProtocolMessageFilterForTesting
        {
            get
            {
                var mco = this.host.Services.GetRequiredService<MultiClusterOracle>();
                return mco.ProtocolMessageFilterForTesting;
            }
            set
            {
                var mco = this.host.Services.GetRequiredService<MultiClusterOracle>();
                mco.ProtocolMessageFilterForTesting = value;
            }
        }

        private bool ShouldDrop(Message msg)
        {
            if (simulatedMessageLoss != null)
            {
                double blockedpercentage;
                simulatedMessageLoss.TryGetValue(msg.TargetSilo.Endpoint, out blockedpercentage);
                return (random.NextDouble() * 100 < blockedpercentage);
            }
            else
                return false;
        }
    }
}
