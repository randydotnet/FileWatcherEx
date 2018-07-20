﻿/*---------------------------------------------------------
 * Copyright (C) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------*/


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace FileWatcherEx
{
    public class EventProcessor
    {

        /// <summary>
        /// Aggregate and only emit events when changes have stopped for this duration (in ms)
        /// </summary>
        private static int EVENT_DELAY = 50;

        /// <summary>
        /// Warn after certain time span of event spam (in ticks)
        /// </summary>
        private static int EVENT_SPAM_WARNING_THRESHOLD = 60 * 1000 * 10000;

        private System.Object LOCK = new System.Object();
        private Task delayTask = null;

        private List<FileEvent> events = new List<FileEvent>();
        private Action<FileEvent> handleEvent;

        private Action<string> logger;

        private long lastEventTime = 0;
        private long delayStarted = 0;

        private long spamCheckStartTime = 0;
        private bool spamWarningLogged = false;




        private IEnumerable<FileEvent> NormalizeEvents(FileEvent[] events)
        {
            var mapPathToEvents = new Dictionary<string, FileEvent>();
            var eventsWithoutDuplicates = new List<FileEvent>();

            // Normalize Duplicates
            foreach (var e in events)
            {

                // Existing event
                if (mapPathToEvents.ContainsKey(e.Path))
                {
                    var existingEvent = mapPathToEvents[e.Path];
                    var currentChangeType = existingEvent.ChangeType;
                    var newChangeType = e.ChangeType;

                    // ignore CREATE followed by DELETE in one go
                    if (currentChangeType == ChangeType.CREATED && newChangeType == ChangeType.DELETED)
                    {
                        mapPathToEvents.Remove(existingEvent.Path);
                        eventsWithoutDuplicates.Remove(existingEvent);
                    }

                    // flatten DELETE followed by CREATE into CHANGE
                    else if (currentChangeType == ChangeType.DELETED && newChangeType == ChangeType.CREATED)
                    {
                        existingEvent.ChangeType = ChangeType.CHANGED;
                    }

                    // Do nothing. Keep the created event
                    else if (currentChangeType == ChangeType.CREATED && newChangeType == ChangeType.CHANGED)
                    {
                    }

                    // Otherwise apply change type
                    else
                    {
                        existingEvent.ChangeType = newChangeType;
                    }
                }

                // New event
                else
                {
                    mapPathToEvents.Add(e.Path, e);
                    eventsWithoutDuplicates.Add(e);
                }
            }

            // Handle deletes
            var addedChangeEvents = new List<FileEvent>();
            var deletedPaths = new List<string>();

            // This algorithm will remove all DELETE events up to the root folder
            // that got deleted if any. This ensures that we are not producing
            // DELETE events for each file inside a folder that gets deleted.
            //
            // 1.) split ADD/CHANGE and DELETED events
            // 2.) sort short deleted paths to the top
            // 3.) for each DELETE, check if there is a deleted parent and ignore the event in that case

            return eventsWithoutDuplicates
                .Where((e) =>
                {
                    if (e.ChangeType != ChangeType.DELETED)
                    {
                        addedChangeEvents.Add(e);
                        return false; // remove ADD / CHANGE
                    }

                    return true;
                })
                .OrderBy((e) => e.Path.Length) // shortest path first
                .Where((e) =>
                {
                    if (deletedPaths.Any(d => IsParent(e.Path, d)))
                    {
                        return false; // DELETE is ignored if parent is deleted already
                    }

                    // otherwise mark as deleted
                    deletedPaths.Add(e.Path);

                    return true;
                })
                .Concat(addedChangeEvents);
        }


        private bool IsParent(string p, string candidate)
        {
            return p.IndexOf(candidate + '\\') == 0;
        }




        public EventProcessor(Action<FileEvent> onEvent, Action<string> onLogging)
        {
            handleEvent = onEvent;
            logger = onLogging;
        }


        public void ProcessEvent(FileEvent fileEvent)
        {
            lock (LOCK)
            {
                var now = DateTime.Now.Ticks;

                // Check for spam
                if (events.Count == 0) {
                    spamWarningLogged = false;
                    spamCheckStartTime = now;
                } else if (!spamWarningLogged && spamCheckStartTime + EVENT_SPAM_WARNING_THRESHOLD < now) {
                    spamWarningLogged = true;
                    logger(string.Format("Warning: Watcher is busy catching up wit {0} file changes in 60 seconds. Latest path is '{1}'", events.Count, fileEvent.Path));
                }

                // Add into our queue
                events.Add(fileEvent);
                lastEventTime = now;

                // Process queue after delay
                if (delayTask == null)
                {
                    // Create function to buffer events
                    Action<Task> func = null;
                    func = (Task value) => {
                        lock (LOCK)
                        {
                            // Check if another event has been received in the meantime
                            if (delayStarted == lastEventTime)
                            {
                                // Normalize and handle
                                var normalized = NormalizeEvents(events.ToArray());
                                foreach (var e in normalized)
                                {
                                    handleEvent(e);
                                }

                                // Reset
                                events.Clear();
                                delayTask = null;
                            }

                            // Otherwise we have received a new event while this task was
                            // delayed and we reschedule it.
                            else
                            {
                                delayStarted = lastEventTime;
                                delayTask = Task.Delay(EVENT_DELAY).ContinueWith(func);
                            }
                        }
                    };

                    // Start function after delay
                    delayStarted = lastEventTime;
                    delayTask = Task.Delay(EVENT_DELAY).ContinueWith(func);                    
                }
            }
        }

        
    }
}
