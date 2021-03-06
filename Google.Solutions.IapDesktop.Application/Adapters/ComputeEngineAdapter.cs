﻿//
// Copyright 2020 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Apis.Compute.v1;
using Google.Apis.Compute.v1.Data;
using Google.Apis.Services;
using Google.Solutions.Compute;
using Google.Solutions.Compute.Extensions;
using Google.Solutions.IapDesktop.Application.ObjectModel;
using Google.Solutions.IapDesktop.Application.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Solutions.IapDesktop.Application.Adapters
{
    public interface IComputeEngineAdapter : IDisposable
    {
        Task<IEnumerable<Instance>> QueryInstancesAsync(string projectId);

        Task<NetworkCredential> ResetWindowsUserAsync(
            VmInstanceReference instanceRef,
            string username,
            CancellationToken token);

        SerialPortStream GetSerialPortOutput(VmInstanceReference instanceRef);
    }

    /// <summary>
    /// Adapter class for the Compute Engine API.
    /// </summary>
    public class ComputeEngineAdapter : IComputeEngineAdapter
    {
        private const string WindowsCloudLicenses = "https://www.googleapis.com/compute/v1/projects/windows-cloud/global/licenses";

        private readonly ComputeService service;

        public ComputeEngineAdapter(IAuthorizationService authService)
        {
            var assemblyName = typeof(IComputeEngineAdapter).Assembly.GetName();
            this.service = new ComputeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = authService.Authorization.Credential,
                ApplicationName = $"{assemblyName.Name}/{assemblyName.Version}"
            });
        }

        public ComputeEngineAdapter(IServiceProvider serviceProvider)
            : this(serviceProvider.GetService<IAuthorizationService>())
        {
        }

        public async Task<IEnumerable<Instance>> QueryInstancesAsync(string projectId)
        {
            using (TraceSources.IapDesktop.TraceMethod().WithParameters(projectId))
            {
                try
                {
                    var zones = await PageHelper.JoinPagesAsync<
                                InstancesResource.AggregatedListRequest,
                                InstanceAggregatedList,
                                InstancesScopedList>(
                        this.service.Instances.AggregatedList(projectId),
                        instances => instances.Items.Values.Where(v => v != null),
                        response => response.NextPageToken,
                        (request, token) => { request.PageToken = token; });

                    var result = zones
                        .Where(z => z.Instances != null)    // API returns null for empty zones.
                        .SelectMany(zone => zone.Instances);


                    TraceSources.IapDesktop.TraceVerbose("Found {0} instances", result.Count());

                    return result;
                }
                catch (GoogleApiException e) when (e.Error != null && e.Error.Code == 403)
                {
                    throw new ComputeEngineException(
                        $"Access to VM instances in project {projectId} has been denied", e);
                }
            }
        }

        public async Task<Instance> QueryInstanceAsync(string projectId, string zone, string instanceName)
        {
            using (TraceSources.IapDesktop.TraceMethod().WithParameters(projectId, zone, instanceName))
            {
                return await this.service.Instances.Get(projectId, zone, instanceName).ExecuteAsync();
            }
        }

        public async Task<Instance> QueryInstanceAsync(VmInstanceReference instanceRef)
        {
            using (TraceSources.IapDesktop.TraceMethod().WithParameters(instanceRef))
            {
                return await QueryInstanceAsync(instanceRef.ProjectId, instanceRef.Zone, instanceRef.InstanceName);
            }
        }

        public SerialPortStream GetSerialPortOutput(VmInstanceReference instanceRef)
        {
            using (TraceSources.IapDesktop.TraceMethod().WithParameters(instanceRef))
            {
                return this.service.Instances.GetSerialPortOutputStream(instanceRef, 1);
            }
        }

        public async Task<NetworkCredential> ResetWindowsUserAsync(
            VmInstanceReference instanceRef,
            string username,
            CancellationToken token)
        {
            using (TraceSources.IapDesktop.TraceMethod().WithParameters(instanceRef))
            {
                return await this.service.Instances.ResetWindowsUserAsync(instanceRef, username, token);
            }
        }

        private static bool IsWindowsInstanceByGuestOsFeature(Instance instance)
        {
            // For an instance to be a valid Windows instance, at least one of the disks
            // (the boot disk) has to be marked as "WINDOWS". 
            // Note that older disks might lack this feature.
            return instance.Disks
                .EnsureNotNull()
                .Where(d => d.GuestOsFeatures != null)
                .SelectMany(d => d.GuestOsFeatures)
                .EnsureNotNull()
                .Any(f => f.Type == "WINDOWS");
        }

        private static bool IsWindowsInstanceByLicense(Instance instance)
        {
            // For an instance to be a valid Windows instance, at least one of the disks
            // has to have an associated Windows license. This is also true for
            // BYOL'ed instances.
            return instance.Disks
                .EnsureNotNull()
                .Where(d => d.Licenses != null)
                .SelectMany(d => d.Licenses)
                .EnsureNotNull()
                .Any(l => l.StartsWith(WindowsCloudLicenses));
        }

        public static bool IsWindowsInstance(Instance instance)
        {
            return IsWindowsInstanceByGuestOsFeature(instance) ||
                   IsWindowsInstanceByLicense(instance);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.service.Dispose();
            }
        }
    }

    [Serializable]
    public class ComputeEngineException : Exception
    {
        protected ComputeEngineException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        public ComputeEngineException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
