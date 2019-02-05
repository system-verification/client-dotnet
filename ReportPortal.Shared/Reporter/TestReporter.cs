﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReportPortal.Client;
using ReportPortal.Client.Requests;
using System.Collections.Generic;
using ReportPortal.Client.Models;

namespace ReportPortal.Shared.Reporter
{
    public class TestReporter : ITestReporter
    {
        private readonly Service _service;

        public TestReporter(Service service, ILaunchReporter launchReporter, ITestReporter parentTestReporter)
        {
            _service = service;
            LaunchReporter = launchReporter;
            ParentTestReporter = parentTestReporter;

            ThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public TestItem TestInfo { get; private set; }

        public ILaunchReporter LaunchReporter { get; }

        public ITestReporter ParentTestReporter { get; }

        public Task StartTask { get; private set; }

        public void Start(StartTestItemRequest request)
        {
            if (StartTask != null)
            {
                throw new InsufficientExecutionStackException("The test item is already scheduled for starting.");
            }

            var dependentTasks = new List<Task>();
            dependentTasks.Add(LaunchReporter.StartTask);
            if (ParentTestReporter != null)
            {
                dependentTasks.Add(ParentTestReporter.StartTask);
            }

            StartTask = Task.Factory.ContinueWhenAll(dependentTasks.ToArray(), async (a) =>
            {
                try
                {
                    Task.WaitAll(dependentTasks.ToArray());
                }
                catch (Exception exp)
                {
                    var aggregatedExp = exp as AggregateException;
                    if (aggregatedExp != null)
                    {
                        exp = aggregatedExp.Flatten();
                    }

                    throw new Exception("Cannot start a test item due parent failed to start.", exp);
                }

                request.LaunchId = LaunchReporter.LaunchInfo.Id;
                if (ParentTestReporter == null)
                {
                    if (request.StartTime < LaunchReporter.LaunchInfo.StartTime)
                    {
                        request.StartTime = LaunchReporter.LaunchInfo.StartTime;
                    }

                    var id = (await _service.StartTestItemAsync(request)).Id;

                    TestInfo = new TestItem
                    {
                        Id = id
                    };
                }
                else
                {
                    if (request.StartTime < ParentTestReporter.TestInfo.StartTime)
                    {
                        request.StartTime = ParentTestReporter.TestInfo.StartTime;
                    }

                    var id = (await _service.StartTestItemAsync(ParentTestReporter.TestInfo.Id, request)).Id;

                    TestInfo = new TestItem
                    {
                        Id = id
                    };
                }

                TestInfo.StartTime = request.StartTime;

            }).Unwrap();
        }

        public Task FinishTask { get; private set; }
        public void Finish(FinishTestItemRequest request)
        {
            if (StartTask == null)
            {
                throw new InsufficientExecutionStackException("The test item wasn't scheduled for starting to finish it properly.");
            }

            if (FinishTask != null)
            {
                throw new InsufficientExecutionStackException("The test item is already scheduled for finishing.");
            }

            var dependentTasks = new List<Task>();
            dependentTasks.Add(StartTask);
            if (_additionalTasks != null)
            {
                dependentTasks.AddRange(_additionalTasks);
            }
            if (ChildTestReporters != null)
            {
                dependentTasks.AddRange(ChildTestReporters.Select(tn => tn.FinishTask));
            }

            FinishTask = Task.Factory.ContinueWhenAll(dependentTasks.ToArray(), async (a) =>
            {
                TestInfo.EndTime = request.EndTime;
                TestInfo.Status = request.Status;

                try
                {
                    StartTask.Wait();
                }
                catch (Exception exp)
                {
                    var aggregatedExp = exp as AggregateException;
                    if (aggregatedExp != null)
                    {
                        exp = aggregatedExp.Flatten();
                    }

                    throw new Exception("Cannot finish test item due starting item failed.", exp);
                }

                try
                {
                    if (ChildTestReporters != null)
                    {
                        Task.WaitAll(ChildTestReporters.Select(tn => tn.FinishTask).ToArray());
                    }
                }
                catch (Exception exp)
                {
                    var aggregatedExp = exp as AggregateException;
                    if (aggregatedExp != null)
                    {
                        exp = aggregatedExp.Flatten();
                    }

                    throw new Exception("Cannot finish test item due finishing of child items failed.", exp);
                }
                finally
                {
                    // clean up childs
                    ChildTestReporters = null;

                    // clean up addition tasks
                    _additionalTasks = null;
                }

                if (request.EndTime < TestInfo.StartTime)
                {
                    request.EndTime = TestInfo.StartTime;
                }

                await _service.FinishTestItemAsync(TestInfo.Id, request);
            }).Unwrap();
        }

        private ConcurrentBag<Task> _additionalTasks;

        public ConcurrentBag<ITestReporter> ChildTestReporters { get; private set; }

        public ITestReporter StartChildTestReporter(StartTestItemRequest request)
        {
            var newTestNode = new TestReporter(_service, LaunchReporter, this);
            newTestNode.Start(request);
            if (ChildTestReporters == null)
            {
                ChildTestReporters = new ConcurrentBag<ITestReporter>();
            }
            ChildTestReporters.Add(newTestNode);

            (LaunchReporter as LaunchReporter).LastTestNode = newTestNode;

            return newTestNode;
        }

        public void Update(UpdateTestItemRequest request)
        {
            if (FinishTask == null || !FinishTask.IsCompleted)
            {
                if (_additionalTasks == null)
                {
                    _additionalTasks = new ConcurrentBag<Task>();
                }
                _additionalTasks.Add(StartTask.ContinueWith(async (a) =>
                {
                    await _service.UpdateTestItemAsync(TestInfo.Id, request);
                }).Unwrap());
            }
        }

        public void Log(AddLogItemRequest request)
        {
            if (StartTask == null)
            {
                throw new InsufficientExecutionStackException("The test item wasn't scheduled for starting to add log messages.");
            }

            if (FinishTask == null || !FinishTask.IsCompleted)
            {
                var dependentTasks = new List<Task>();
                dependentTasks.Add(StartTask);
                if (_additionalTasks == null)
                {
                    _additionalTasks = new ConcurrentBag<Task>();
                }
                dependentTasks.AddRange(_additionalTasks);

                var task = Task.Factory.ContinueWhenAll(dependentTasks.ToArray(), async (t) =>
                {
                    StartTask.Wait();

                    if (request.Time < TestInfo.StartTime)
                    {
                        request.Time = TestInfo.StartTime;
                    }

                    request.TestItemId = TestInfo.Id;

                    await _service.AddLogItemAsync(request);
                }).Unwrap();

                _additionalTasks.Add(task);
            }
        }

        // TODO: need remove (used by specflow only)
        public int ThreadId { get; set; }
    }

}
