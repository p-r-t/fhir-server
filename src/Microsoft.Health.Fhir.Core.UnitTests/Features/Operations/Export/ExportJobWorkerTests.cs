﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Export;
using Microsoft.Health.Fhir.Core.Features.Operations.Export.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Operations.Export
{
    public class ExportJobWorkerTests
    {
        private const ushort DefaultMaximumNumberOfConcurrentJobAllowed = 1;
        private static readonly TimeSpan DefaultJobHeartbeatTimeoutThreshold = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DefaultJobPollingFrequency = TimeSpan.FromMilliseconds(100);

        private readonly IFhirOperationsDataStore _fhirOperationsDataStore = Substitute.For<IFhirOperationsDataStore>();
        private readonly ExportJobConfiguration _exportJobConfiguration = new ExportJobConfiguration();
        private readonly IExportJobTaskFactory _exportJobTaskFactory = Substitute.For<IExportJobTaskFactory>();

        private readonly ExportJobWorker _exportJobWorker;

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly CancellationToken _cancellationToken;

        public ExportJobWorkerTests()
        {
            _exportJobConfiguration.MaximumNumberOfConcurrentJobsAllowed = DefaultMaximumNumberOfConcurrentJobAllowed;
            _exportJobConfiguration.JobHeartbeatTimeoutThreshold = DefaultJobHeartbeatTimeoutThreshold;
            _exportJobConfiguration.JobPollingFrequency = DefaultJobPollingFrequency;

            _exportJobWorker = new ExportJobWorker(
                _fhirOperationsDataStore,
                Options.Create(_exportJobConfiguration),
                _exportJobTaskFactory,
                NullLogger<ExportJobWorker>.Instance);

            _cancellationToken = _cancellationTokenSource.Token;
        }

        [Fact]
        public async Task GivenThereIsNoRunningJob_WhenExecuted_ThenATaskShouldBeCreated()
        {
            ExportJobOutcome job = CreateExportJobOutcome();

            SetupOperationsDataStore(job);

            _exportJobTaskFactory.Create(job.JobRecord, job.ETag, _cancellationToken).Returns(Task.CompletedTask);

            _cancellationTokenSource.CancelAfter(DefaultJobPollingFrequency);

            await _exportJobWorker.ExecuteAsync(_cancellationToken);

            await _exportJobTaskFactory.Received().Create(job.JobRecord, job.ETag, _cancellationToken);
        }

        [Fact]
        public async Task GivenTheNumberOfRunningJobExceedsThreshold_WhenExecuted_ThenATaskShouldNotBeCreated()
        {
            ExportJobOutcome job = CreateExportJobOutcome();

            SetupOperationsDataStore(job);

            _exportJobTaskFactory.Create(job.JobRecord, job.ETag, _cancellationToken).Returns(Task.Run(async () => { await Task.Delay(1000); }));

            _cancellationTokenSource.CancelAfter(DefaultJobPollingFrequency * 2);

            await _exportJobWorker.ExecuteAsync(_cancellationToken);

            await _exportJobTaskFactory.Received(1).Create(job.JobRecord, job.ETag, _cancellationToken);
        }

        [Fact]
        public async Task GivenTheNumberOfRunningJobDoesNotExceedThreshold_WhenExecuted_ThenATaskShouldBeCreated()
        {
            const int MaximumNumberOfConcurrentJobsAllowed = 2;

            _exportJobConfiguration.MaximumNumberOfConcurrentJobsAllowed = MaximumNumberOfConcurrentJobsAllowed;

            ExportJobOutcome job1 = CreateExportJobOutcome();
            ExportJobOutcome job2 = CreateExportJobOutcome();

            SetupOperationsDataStore(
                job1,
                maximumNumberOfConcurrentJobsAllowed: MaximumNumberOfConcurrentJobsAllowed);

            _exportJobTaskFactory.Create(job1.JobRecord, job1.ETag, _cancellationToken).Returns(Task.Run(() =>
            {
                // Simulate the fact a new job now becomes available.
                SetupOperationsDataStore(
                    job2,
                    maximumNumberOfConcurrentJobsAllowed: MaximumNumberOfConcurrentJobsAllowed);

                return Task.CompletedTask;
            }));

            bool isSecondJobCalled = false;

            _exportJobTaskFactory.Create(job2.JobRecord, job2.ETag, _cancellationToken).Returns(Task.Run(() =>
            {
                // The task was called and therefore we can cancel the worker.
                isSecondJobCalled = true;

                _cancellationTokenSource.Cancel();

                return Task.CompletedTask;
            }));

            // In case the task was not called, cancel the worker after certain period of time.
            _cancellationTokenSource.CancelAfter(DefaultJobPollingFrequency * 3);

            await _exportJobWorker.ExecuteAsync(_cancellationToken);

            Assert.True(isSecondJobCalled);
        }

        private void SetupOperationsDataStore(
            ExportJobOutcome job,
            ushort maximumNumberOfConcurrentJobsAllowed = DefaultMaximumNumberOfConcurrentJobAllowed,
            TimeSpan? jobHeartbeatTimeoutThreshold = null,
            TimeSpan? jobPollingFrequency = null)
        {
            if (jobHeartbeatTimeoutThreshold == null)
            {
                jobHeartbeatTimeoutThreshold = DefaultJobHeartbeatTimeoutThreshold;
            }

            if (jobPollingFrequency == null)
            {
                jobPollingFrequency = DefaultJobPollingFrequency;
            }

            _fhirOperationsDataStore.GetAvailableExportJobsAsync(
                maximumNumberOfConcurrentJobsAllowed,
                jobHeartbeatTimeoutThreshold.Value,
                _cancellationToken)
                .Returns(new[] { job });
        }

        private ExportJobOutcome CreateExportJobOutcome()
        {
            return new ExportJobOutcome(new ExportJobRecord(new Uri($"http://localhost/ExportJob/")), WeakETag.FromVersionId("0"));
        }
    }
}
