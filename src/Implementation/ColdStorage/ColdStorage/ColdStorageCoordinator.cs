﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Practices.IoTJourney.ColdStorage.Logging;
using Microsoft.Practices.IoTJourney.ColdStorage.RollingBlobWriter;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Microsoft.WindowsAzure.Storage;

namespace Microsoft.Practices.IoTJourney.ColdStorage
{
    public class ColdStorageCoordinator : IDisposable
    {
        private EventProcessorHost _host;

        private ColdStorageCoordinator(EventProcessorHost host)
        {
            _host = host;
        }

        public static async Task<ColdStorageCoordinator> CreateAsync(string hostName, Configuration configuration)
        {
            ColdStorageEventSource.Log.InitializingEventHubListener(configuration.EventHubName, configuration.ConsumerGroupName);

            var storageAccount = CloudStorageAccount.Parse(configuration.BlobWriterStorageAccount);

            Func<string, IBlobWriter> blobWriterFactory =
                    partitionId =>
                        new RollingBlobWriter.RollingBlobWriter(new PartitionAndDateNamingStrategy(partitionId, configuration.BlobPrefix),
                            storageAccount,
                            configuration.ContainerName,
                            configuration.RollSizeForBlobWriterMb);

            var ns = NamespaceManager.CreateFromConnectionString(configuration.EventHubConnectionString);
            try
            {
                await ns.GetConsumerGroupAsync(configuration.EventHubName, configuration.ConsumerGroupName);
            }
            catch (Exception e)
            {
                ColdStorageEventSource.Log.InvalidEventHubConsumerGroupName(e, configuration.EventHubName, configuration.ConsumerGroupName);
                throw;
            }

            ColdStorageEventSource.Log.ConsumerGroupFound(configuration.EventHubName, configuration.ConsumerGroupName);

            var eventHubId = ConfigurationHelper.GetEventHubName(ns.Address, configuration.EventHubName);

            var factory = new ColdStorageEventProcessorFactory(
                blobWriterFactory,
                CancellationToken.None,
                configuration.CircuitBreakerWarningLevel,
                configuration.CircuitBreakerTripLevel,
                configuration.CircuitBreakerStallInterval,
                configuration.CircuitBreakerLogCooldownInterval,
                eventHubId
            );

            var options = new EventProcessorOptions()
            {
                MaxBatchSize = configuration.MaxBatchSize,
                PrefetchCount = configuration.PreFetchCount,
                ReceiveTimeOut = configuration.ReceiveTimeout,
                InvokeProcessorAfterReceiveTimeout = true
            };

            options.ExceptionReceived += 
                (s, e) => ColdStorageEventSource.Log.ErrorProcessingMessage(e.Exception, e.Action);
          
            var host = new EventProcessorHost(
                hostName,
                consumerGroupName: configuration.ConsumerGroupName,
                eventHubPath: configuration.EventHubName,
                eventHubConnectionString: configuration.EventHubConnectionString,
                storageConnectionString: configuration.CheckpointStorageAccount);


            await host.RegisterEventProcessorFactoryAsync(factory, options);

            return new ColdStorageCoordinator(host);
        }

        public void Dispose()
        {
            if (_host != null)
            {
                _host.UnregisterEventProcessorAsync().Wait();
                _host = null;
            }
        }
    }
}
