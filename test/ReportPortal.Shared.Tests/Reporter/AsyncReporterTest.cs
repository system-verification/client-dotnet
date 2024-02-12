﻿using FluentAssertions;
using Moq;
using ReportPortal.Client.Abstractions.Requests;
using ReportPortal.Client.Abstractions.Responses;
using ReportPortal.Shared.Configuration;
using ReportPortal.Shared.Extensibility;
using ReportPortal.Shared.Reporter;
using ReportPortal.Shared.Tests.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReportPortal.Shared.Tests.Reporter
{
    public class AsyncReporterTest
    {
        private readonly IConfiguration _configuration;

        public AsyncReporterTest()
        {
            _configuration = new ConfigurationBuilder().Build();
            _configuration.Properties[ConfigurationPath.AsyncReporting] = true;
        }

        [Theory]
        [InlineData(1, 1, 0)]
        [InlineData(1, 1, 1)]
        [InlineData(5, 100, 0)]
        [InlineData(10, 200, 10)]
        public void SuccessReporting(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object).WithConfiguration(_configuration);
            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            launchReporter.Sync();

            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch + suitesPerLaunch));

            service.Verify(s => s.TestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Never());
            service.Verify(s => s.TestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Never());
            service.Verify(s => s.TestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Never());

            launchReporter.ChildTestReporters.Select(s => s.Info.Uuid).Should().OnlyHaveUniqueItems();
            launchReporter.ChildTestReporters.SelectMany(s => s.ChildTestReporters).Select(t => t.Info.Uuid).Should().OnlyHaveUniqueItems();
        }

        [Theory]
        [InlineData(1, 1, 10)]
        public void ShouldInvokeSingleVersionOfEndpoint(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object).WithConfiguration(_configuration);
            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            launchReporter.Sync();

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Once());
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch + suitesPerLaunch));
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once());

            service.Verify(s => s.Launch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Never());
            service.Verify(s => s.TestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Never());
            service.Verify(s => s.TestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Never());
            service.Verify(s => s.TestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Never());
            service.Verify(s => s.Launch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Never());
        }

        [Fact]
        public void ShouldFulfillTestInfo()
        {
            var now = DateTime.UtcNow;

            var service = new MockServiceBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object).WithConfiguration(_configuration);
            var launchReporter = launchScheduler.Build(1, 0, 0);

            launchReporter.Sync();

            var testReporter = launchReporter.ChildTestReporters[0];

            testReporter.Should().NotBeNull();
            testReporter.Info.Uuid.Should().NotBeNullOrEmpty();
            testReporter.Info.Name.Should().NotBeNullOrEmpty();
            testReporter.Info.StartTime.Should().BeCloseTo(now, precision: TimeSpan.FromMilliseconds(100));
            testReporter.Info.FinishTime.Should().BeCloseTo(now, precision: TimeSpan.FromMilliseconds(100));
            (testReporter.Info as TestInfo).Status.Should().Be(Client.Abstractions.Models.Status.Passed);
        }

        [Fact]
        public void MixingTestsAndLogs()
        {
            var service = new MockServiceBuilder().Build();

            var launchReporter = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            launchReporter.Start(new StartLaunchRequest { StartTime = DateTime.UtcNow });

            for (int i = 0; i < 10; i++)
            {
                var suite = launchReporter.StartChildTestReporter(new StartTestItemRequest { StartTime = DateTime.UtcNow });

                suite.Log(new CreateLogItemRequest { Time = DateTime.UtcNow });

                for (int j = 0; j < 20; j++)
                {
                    var test = suite.StartChildTestReporter(new StartTestItemRequest { StartTime = DateTime.UtcNow });

                    test.Log(new CreateLogItemRequest { Time = DateTime.UtcNow });

                    test.Finish(new FinishTestItemRequest { EndTime = DateTime.UtcNow });
                }

                suite.Log(new CreateLogItemRequest { Time = DateTime.UtcNow });

                suite.Finish(new FinishTestItemRequest { EndTime = DateTime.UtcNow });
            }

            launchReporter.Finish(new FinishLaunchRequest { EndTime = DateTime.UtcNow });

            launchReporter.Sync();
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(10, 10, 10)]
        public void FailedLogsShouldNotAffectFinishingLaunch(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncLogItem.CreateAsync(It.IsAny<CreateLogItemRequest[]>(), default)).Throws<Exception>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            launchReporter.Sync();

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Exactly(1));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch + suitesPerLaunch));
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once);
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(10, 10, 10)]
        public void CanceledLogsShouldNotAffectFinishingLaunch(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.LogItem.CreateAsync(It.IsAny<CreateLogItemRequest>(), default)).Throws<TaskCanceledException>();
            service.Setup(s => s.LogItem.CreateAsync(It.IsAny<CreateLogItemRequest[]>(), default)).Throws<TaskCanceledException>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            launchReporter.Sync();

            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Exactly(testsPerSuite * suitesPerLaunch + suitesPerLaunch));
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once);
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(1, 1, 2)]
        [InlineData(5, 5, 0)]
        public void FailedFinishTestItemShouldRaiseExceptionAtFinishLaunch(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default)).Throws(new Exception());

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
            Assert.Contains("Cannot finish launch", exp.Message);

            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch * testsPerSuite));
            service.Verify(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Exactly(suitesPerLaunch * testsPerSuite));
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(10, 10, 2)]
        [InlineData(50, 50, 0)]
        public void CanceledFinishTestItemShouldRaiseExceptionAtFinishLaunch(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default)).Throws<TaskCanceledException>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
            Assert.Contains("Cannot finish launch", exp.Message);

            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch * testsPerSuite));
            service.Verify(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default), Times.Exactly(suitesPerLaunch * testsPerSuite));
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Never);
        }

        [Theory]
        [InlineData(100, 1, 1)]
        [InlineData(1, 100, 100)]
        [InlineData(100, 10, 1)]
        public void FailedStartSuiteItemShouldRaiseExceptionAtFinishLaunch(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default)).Throws<Exception>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
            Assert.Contains("Cannot finish launch", exp.Message);

            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(null, It.IsAny<StartTestItemRequest>(), default), Times.Never);
            service.Verify(s => s.AsyncTestItem.FinishAsync(null, It.IsAny<FinishTestItemRequest>(), default), Times.Never);
        }

        [Theory]
        [InlineData(100, 1, 1)]
        [InlineData(1, 10, 100)]
        [InlineData(10, 10, 1)]
        public void CanceledStartSuiteItemShouldRaiseExceptionAtFinishLaunch(int suitesPerLaunch, int testsPerSuite, int logsPerTest)
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default)).Throws<TaskCanceledException>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(suitesPerLaunch, testsPerSuite, logsPerTest);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
            Assert.Contains("Cannot finish launch", exp.Message);

            service.Verify(s => s.AsyncTestItem.StartAsync(It.IsAny<StartTestItemRequest>(), default), Times.Exactly(suitesPerLaunch));
            service.Verify(s => s.AsyncTestItem.StartAsync(null, It.IsAny<StartTestItemRequest>(), default), Times.Never);
            service.Verify(s => s.AsyncTestItem.FinishAsync(null, It.IsAny<FinishTestItemRequest>(), default), Times.Never);
            service.Verify(s => s.AsyncLaunch.FinishAsync(null, It.IsAny<FinishLaunchRequest>(), default), Times.Never);
        }

        [Fact]
        public void StartLaunchScheduling()
        {
            var service = new MockServiceBuilder().Build();

            var launchReporters = new List<Mock<LaunchReporter>>();

            for (int i = 0; i < 100; i++)
            {
                var launchReporter = new Mock<LaunchReporter>(service.Object, _configuration, null, new ExtensionManager());

                launchReporter.Object.Start(new StartLaunchRequest
                {
                    Name = $"ReportPortal Shared {i}",
                    StartTime = DateTime.UtcNow
                });

                launchReporters.Add(launchReporter);
            }

            for (int i = 0; i < 100; i++)
            {
                var launchReporter = launchReporters[i];

                Assert.NotNull(launchReporter.Object.StartTask);

                launchReporter.Object.Sync();

                Assert.Equal($"ReportPortal Shared {i}", launchReporter.Object.Info.Name);
            }

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Exactly(100));
        }

        [Fact]
        public void ShouldSyncNotStartedLaunch()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());

            launch.Sync();
        }

        [Fact]
        public void StartLaunchTimeout()
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default)).Throws<TaskCanceledException>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(1, 1, 1);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
        }

        [Fact]
        public void StartTestItemTimeout()
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncTestItem.StartAsync(It.IsAny<string>(), It.IsAny<StartTestItemRequest>(), default)).Throws<TaskCanceledException>();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(new MockRequestExecuterFactoryBuilder().Build().Object);

            var launchReporter = launchScheduler.Build(1, 1, 1);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
            Assert.Contains("Cannot finish launch", exp.Message);
        }

        [Fact]
        public void FinishTestItemTimeout()
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncTestItem.FinishAsync(It.IsAny<string>(), It.IsAny<FinishTestItemRequest>(), default)).Throws<TaskCanceledException>();

            var requestExecuterFactory = new MockRequestExecuterFactoryBuilder().Build();

            var launchScheduler = new LaunchReporterBuilder(service.Object)
                .WithConfiguration(_configuration)
                .With(requestExecuterFactory.Object);

            var launchReporter = launchScheduler.Build(1, 1, 1);

            var exp = Assert.ThrowsAny<Exception>(() => launchReporter.Sync());
            Assert.Contains("Cannot finish launch", exp.Message);

            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Never);
        }

        [Fact]
        public void LogsReportingShouldBeOneByOne()
        {
            var requests = new ConcurrentBag<CreateLogItemRequest[]>();

            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncLogItem.CreateAsync(It.IsAny<CreateLogItemRequest[]>(), default))
                .ReturnsAsync(new LogItemsCreatedResponse())
                .Callback<CreateLogItemRequest[], CancellationToken>((arg, t) => requests.Add(arg));

            var launchScheduler = new LaunchReporterBuilder(service.Object).WithConfiguration(_configuration);
            var launchReporter = launchScheduler.Build(1, 30, 30);

            launchReporter.Sync();

            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once);
            service.Verify(s => s.AsyncLogItem.CreateAsync(It.IsAny<CreateLogItemRequest[]>(), default), Times.AtLeast(30 * 30 / 20)); // logs batch size

            foreach (var bufferedRequests in requests)
            {
                bufferedRequests.Select(r => r.Text).Should().BeInAscendingOrder();
            }
        }

        [Fact]
        public void FinishLaunchWhenChildTestItemIsNotScheduledToFinish()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            launch.Start(new StartLaunchRequest { });

            var test = launch.StartChildTestReporter(new StartTestItemRequest { });

            var exp = Assert.Throws<InsufficientExecutionStackException>(() => launch.Finish(new FinishLaunchRequest { }));
            Assert.Contains("are not scheduled to finish yet", exp.Message);
        }

        [Fact]
        public void FinishLaunchWhichIsAlreadyFinished()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest { });
            launch.Finish(new FinishLaunchRequest());
            launch.Invoking(l => l.Finish(new FinishLaunchRequest())).Should().Throw<InsufficientExecutionStackException>().And.Message.Should().Contain("already scheduled for finishing");
        }

        [Fact]
        public void ShouldThrowExceptionWhenStartingAlreadyStartedTestItem()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest());
            var test = launch.StartChildTestReporter(new StartTestItemRequest());
            test.Invoking(t => t.Start(new StartTestItemRequest())).Should().ThrowExactly<InsufficientExecutionStackException>();
        }

        [Fact]
        public void ShouldRerunLaunch()
        {
            var service = new MockServiceBuilder().Build();

            StartLaunchRequest startLaunchRequest = null;

            service.Setup(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default))
                .Returns(() => Task.FromResult(new LaunchCreatedResponse { Uuid = Guid.NewGuid().ToString() }))
                .Callback<StartLaunchRequest, CancellationToken>((r, t) => startLaunchRequest = r);

            var config = new ConfigurationBuilder().Build();

            config.Properties[ConfigurationPath.AsyncReporting] = true;
            config.Properties["Launch:Rerun"] = "true";

            var launch = new LaunchReporter(service.Object, config, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });
            launch.Finish(new FinishLaunchRequest() { EndTime = DateTime.UtcNow });
            launch.Sync();

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Once);
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once);

            startLaunchRequest.IsRerun.Should().BeTrue();
            startLaunchRequest.RerunOfLaunchUuid.Should().BeNull();
        }

        [Fact]
        public void ShouldRerunOfLaunchOnlyIfRerunIsSet()
        {
            var service = new MockServiceBuilder().Build();

            StartLaunchRequest startLaunchRequest = null;

            service.Setup(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default))
                .Returns(() => Task.FromResult(new LaunchCreatedResponse { Uuid = Guid.NewGuid().ToString() }))
                .Callback<StartLaunchRequest, CancellationToken>((r, t) => startLaunchRequest = r);

            var config = new ConfigurationBuilder().Build();

            config.Properties[ConfigurationPath.AsyncReporting] = true;
            config.Properties["Launch:Rerun"] = "true";
            config.Properties["Launch:RerunOf"] = "any_uuid_of_existing_launch";

            var launch = new LaunchReporter(service.Object, config, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });
            launch.Finish(new FinishLaunchRequest() { EndTime = DateTime.UtcNow });
            launch.Sync();

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Once);
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once);

            startLaunchRequest.IsRerun.Should().BeTrue();
            startLaunchRequest.RerunOfLaunchUuid.Should().Be("any_uuid_of_existing_launch");
        }

        [Fact]
        public void ShouldNotRerunOfLaunchOnlyIfRerunIsSet()
        {
            var service = new MockServiceBuilder().Build();

            StartLaunchRequest startLaunchRequest = null;

            service.Setup(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default))
                .Returns(() => Task.FromResult(new LaunchCreatedResponse { Uuid = Guid.NewGuid().ToString() }))
                .Callback<StartLaunchRequest, CancellationToken>((r, t) => startLaunchRequest = r);

            var config = new ConfigurationBuilder().Build();
    
            config.Properties[ConfigurationPath.AsyncReporting] = true;
            config.Properties["Launch:Rerun"] = "false";
            config.Properties["Launch:RerunOf"] = "any_uuid_of_existing_launch";

            var launch = new LaunchReporter(service.Object, config, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });
            launch.Finish(new FinishLaunchRequest() { EndTime = DateTime.UtcNow });
            launch.Sync();

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Once);
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Once);

            startLaunchRequest.IsRerun.Should().BeFalse();
            startLaunchRequest.RerunOfLaunchUuid.Should().BeNull();
        }

        [Fact]
        public void ShouldUseExternalLaunchEnsteadOfStartingNew()
        {
            var service = new MockServiceBuilder().Build();

            service.Setup(s => s.Launch.GetAsync(It.IsAny<string>(), default))
                .Returns(Task.FromResult(new LaunchResponse { Uuid = "123" }));

            var config = new ConfigurationBuilder().Build();
            config.Properties[ConfigurationPath.AsyncReporting] = true;
            config.Properties["Launch:Id"] = "any_uuid_of_existing_launch";

            var launch = new LaunchReporter(service.Object, config, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });
            launch.Finish(new FinishLaunchRequest() { EndTime = DateTime.UtcNow });
            launch.Sync();

            service.Verify(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default), Times.Never);
            service.Verify(s => s.AsyncLaunch.FinishAsync(It.IsAny<string>(), It.IsAny<FinishLaunchRequest>(), default), Times.Never);

            service.Verify(s => s.Launch.GetAsync(It.IsAny<string>(), default), Times.Once);

            launch.Info.Uuid.Should().Be("123");
        }

        [Fact]
        public void FinishLaunchWhichIsNotStarted()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, null, null, new ExtensionManager());
            launch.Invoking(l => l.Finish(new FinishLaunchRequest())).Should().Throw<InsufficientExecutionStackException>().And.Message.Should().Contain("wasn't scheduled for starting");
        }

        [Fact]
        public void StartingLaunchWhichIsAlreadyStarted()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            launch.Start(new StartLaunchRequest { });
            launch.Invoking(l => l.Start(new StartLaunchRequest { })).Should().Throw<InsufficientExecutionStackException>().And.Message.Should().Contain("already scheduled for starting");
        }

        [Fact]
        public void FinishTestItemWhenChildTestItemIsNotScheduledToFinish()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new Mock<IExtensionManager>().Object);
            launch.Start(new StartLaunchRequest { });
            var test = launch.StartChildTestReporter(new StartTestItemRequest());
            var innerTest = test.StartChildTestReporter(new StartTestItemRequest());

            var exp = Assert.Throws<InsufficientExecutionStackException>(() => test.Finish(new FinishTestItemRequest()));
            Assert.Contains("are not scheduled to finish yet", exp.Message);
        }

        [Fact]
        public void ShouldNotLogIfLaunchFailedToStart()
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.AsyncLaunch.StartAsync(It.IsAny<StartLaunchRequest>(), default)).Throws(new Exception());

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });

            launch.Log(new CreateLogItemRequest { Time = DateTime.UtcNow, Text = "log" });

            Action waitAct = () => launch.Sync();
            waitAct.Should().Throw<Exception>();

            service.Verify(s => s.AsyncLogItem.CreateAsync(It.IsAny<CreateLogItemRequest>(), default), Times.Never);
        }

        [Fact]
        public void FailedLaunchLogShouldNotBreakReporting()
        {
            var service = new MockServiceBuilder().Build();
            service.Setup(s => s.LogItem.CreateAsync(It.IsAny<CreateLogItemRequest>(), default)).Throws(new Exception());

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });
            launch.Log(new CreateLogItemRequest { Time = DateTime.UtcNow, Text = "log" });
            launch.Finish(new FinishLaunchRequest() { EndTime = DateTime.UtcNow });

            launch.Sync();
        }

        [Fact]
        public void ShouldBeAbleToLogIntoLaunch()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            launch.Start(new StartLaunchRequest() { StartTime = DateTime.UtcNow });
            launch.Log(new CreateLogItemRequest { Time = DateTime.UtcNow, Text = "log" });
            launch.Finish(new FinishLaunchRequest() { EndTime = DateTime.UtcNow });
            launch.Sync();

            service.Verify(s => s.AsyncLogItem.CreateAsync(It.IsAny<CreateLogItemRequest[]>(), default), Times.Once);
        }

        [Fact]
        public void CannotLogIfLaunchNotStarted()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());
            Action act = () => launch.Log(new CreateLogItemRequest { Time = DateTime.UtcNow, Text = "log" });

            act.Should().Throw<InsufficientExecutionStackException>().WithMessage("*launch wasn't scheduled for starting*");
        }

        [Fact]
        public void ShouldThrowWhenLaunchStartingNullRequest()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());

            Action act = () => launch.Start(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ShouldThrowWhenLaunchFinishingNullRequest()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());

            launch.Start(new StartLaunchRequest());

            Action act = () => launch.Finish(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ShouldThrowWhenTestStartingNullRequest()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());

            launch.Start(new StartLaunchRequest());

            Action act = () => launch.StartChildTestReporter(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ShouldThrowWhenTestFinishingNullRequest()
        {
            var service = new MockServiceBuilder().Build();

            var launch = new LaunchReporter(service.Object, _configuration, null, new ExtensionManager());

            launch.Start(new StartLaunchRequest());

            var test = launch.StartChildTestReporter(new StartTestItemRequest());

            Action act = () => test.Finish(null);
            act.Should().Throw<ArgumentNullException>();
        }
    }
}
