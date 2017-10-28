﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Vim.Extensions;

namespace Vim.UnitTest.Utilities
{
    public sealed class StaTaskScheduler : TaskScheduler, IDisposable
    {
        /// <summary>Gets a StaTaskScheduler for the current AppDomain.</summary>
        /// <remarks>We use a count of 1, because the editor ends up re-using <see cref="System.Windows.Threading.DispatcherObject"/>
        /// instances between tests, so we need to always use the same thread for our Sta tests.</remarks>
        public static StaTaskScheduler DefaultSta { get; } = new StaTaskScheduler(1);
 
        /// <summary>Stores the queued tasks to be executed by our pool of STA threads.</summary>
        private BlockingCollection<Task> _tasks;
 
        /// <summary>The STA threads used by the scheduler.</summary>
        private readonly ReadOnlyCollection<Thread> _threads;
 
        public ReadOnlyCollection<Thread> Threads => _threads;

        public bool IsRunningInScheduler => _threads.Any(x => x.ManagedThreadId == Thread.CurrentThread.ManagedThreadId);
 
        /// <summary>Initializes a new instance of the StaTaskScheduler class with the specified concurrency level.</summary>
        /// <param name="numberOfThreads">The number of threads that should be created and used by this scheduler.</param>
        public StaTaskScheduler(int numberOfThreads)
        {
            // Validate arguments
            if (numberOfThreads < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(numberOfThreads));
            }
 
            // Initialize the tasks collection
            _tasks = new BlockingCollection<Task>();

            // Create the threads to be used by this scheduler
            _threads = Enumerable.Range(0, numberOfThreads).Select(i =>
            {
                var thread = new Thread(() =>
                {
                    // Continually get the next task and try to execute it.
                    // This will continue until the scheduler is disposed and no more tasks remain.
                    foreach (var t in _tasks.GetConsumingEnumerable())
                    {
                        if (!TryExecuteTask(t))
                        {
                            System.Diagnostics.Debug.Assert(t.IsCompleted, "Can't run, not completed");
                        }
                    }
                });
                thread.Name = $"{nameof(StaTaskScheduler)} thread";
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                return thread;
            }).ToReadOnlyCollection();
 
            // Start all of the threads
            foreach (var thread in _threads)
            {
                thread.Start();
            }
        }
 
        /// <summary>Queues a Task to be executed by this scheduler.</summary>
        /// <param name="task">The task to be executed.</param>
        protected override void QueueTask(Task task)
        {
            // Push it into the blocking collection of tasks
            _tasks.Add(task);
        }
 
        /// <summary>Provides a list of the scheduled tasks for the debugger to consume.</summary>
        /// <returns>An enumerable of all tasks currently scheduled.</returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            // Serialize the contents of the blocking collection of tasks for the debugger
            return _tasks.ToArray();
        }
 
        /// <summary>Determines whether a Task may be inlined.</summary>
        /// <param name="task">The task to be executed.</param>
        /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
        /// <returns>true if the task was successfully inlined; otherwise, false.</returns>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            // Try to inline if the current thread is STA
            return
                Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
                TryExecuteTask(task);
        }
 
        /// <summary>Gets the maximum concurrency level supported by this scheduler.</summary>
        public override int MaximumConcurrencyLevel
        {
            get
            {
                return _threads.Count;
            }
        }
 
        /// <summary>
        /// Cleans up the scheduler by indicating that no more tasks will be queued.
        /// This method blocks until all threads successfully shutdown.
        /// </summary>
        public void Dispose()
        {
            if (_tasks != null)
            {
                // Indicate that no new tasks will be coming in
                _tasks.CompleteAdding();
 
                // Wait for all threads to finish processing tasks
                foreach (var thread in _threads)
                    thread.Join();
 
                // Cleanup
                _tasks.Dispose();
                _tasks = null;
            }
        }
 
        public bool IsAnyQueued()
        {
            if (_threads.Count != 1 || _threads[0] != Thread.CurrentThread)
            {
                throw new InvalidOperationException("Operation invalid in this context");
            }
 
            return _tasks.Count > 0;
        }

    }
}
