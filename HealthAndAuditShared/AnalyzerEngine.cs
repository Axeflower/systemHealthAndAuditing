﻿/****************************************************************************************
*	This code originates from the software development department at					*
*	swedish insurance and private loan broker Insplanet AB.								*
*	Full license available in license.txt												*
*	This text block may not be removed or altered.                                  	*
*	The list of contributors may be extended.                                           *
*																						*
*							Mikael Axblom, head of software development, Insplanet AB	*
*																						*
*	Contributors: Mikael Axblom															*
*****************************************************************************************/
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemHealthExternalInterface;
using HealthAndAuditShared.Observers;
using System.Threading;

namespace HealthAndAuditShared
{
    public enum State
    {
        Running,
        ShuttingDown,
        Stopped
    }

    /// <summary>
    /// The main engine. Holds a collection of <see cref="ProgramAnalyzer"/>s that runs the actual analyses.
    /// </summary>
    public sealed class AnalyzerEngine
    {     

        public State State { get; private set; } = State.Stopped;
        private IRuleStorage RuleStorage { get; set; }
        /// <summary>
        /// Starts the engine. Reads <see cref="AnalyzeRule"/>s from storage and builds a collection of <see cref="ProgramAnalyzer"/>s to hold them.
        /// </summary>
        /// <param name="ruleStorage">The <see cref="AnalyzeRule"/> storage.</param>
        /// <param name="alarmMessageManager">The alarm manager.</param>
        public void StartEngine(IRuleStorage ruleStorage, AlarmMessageManager alarmMessageManager)
        {
            AddMessage($"Starting {nameof(AnalyzerEngine)} {Guid.NewGuid()}");
            RuleStorage = ruleStorage;
            AlarmMessageManager = alarmMessageManager;
            Analyzers.Clear();
            var allRules = RuleStorage.GetAllRules();
            if (allRules.Count == 0)
            {
                AddMessage("Starting with no rules.");
            }
            else
            {
                AddRulesToAnalyzer(allRules);
            }
            foreach(var analyzer in Analyzers)
            {
                analyzer.Value.StartAnalyzerTask();
            }
            StartEngineTask();
        }
        /// <summary>
        /// Stops the engine safely. Letting all current operations complete but will not allow the engine to start any now tasks.
        /// </summary>
        public void StopEngine()
        {
            AddMessage("Initiating engine shutdown.");
            State = State.ShuttingDown;        
            AddMessage("Waiting for all analyzers to finish.");
            var timer = new Stopwatch();
            timer.Start();
            while (Analyzers.Any(a => a.Value.State != State.Stopped) && timer.ElapsedMilliseconds < 300000)
            {
                AddMessage($"{Analyzers.Count(a => a.Value.State != State.Stopped)} analyzers not stopped. Waited {timer.ElapsedMilliseconds} ms.");
                Thread.Sleep(1000);
            }
            AddMessage("Shutdown complete.");
            State = State.Stopped;
        }
        /// <summary>
        /// Messages from the engine. eg: if it has started, events recivied.
        /// </summary>
        /// <value>
        /// The engine messages.
        /// </value>
        public ConcurrentQueue<TimeStampedMessage<string>> EngineMessages { get; } = new ConcurrentQueue<TimeStampedMessage<string>>();
        private AlarmMessageManager AlarmMessageManager { get; set; }
        /// <summary>
        /// Gets a value indicating whether the engines main task is running.
        /// </summary>
        /// <value>
        ///   <c>true</c> if engine is running; otherwise, <c>false</c>.
        /// </value>
        public bool EngineIsRunning  => State == State.Running;        
        /// <summary>
        /// Adds a message to EngineMessageCollection.
        /// </summary>
        /// <param name="message">The message.</param>
        private void AddMessage(string message)
        {
            EngineMessages.Enqueue(new TimeStampedMessage<string>(DateTime.UtcNow, message));
        }
        private ConcurrentQueue<SystemEvent> MainEventQueue { get; } = new ConcurrentQueue<SystemEvent>();
        private ConcurrentDictionary<string, ProgramAnalyzer> Analyzers { get; } = new ConcurrentDictionary<string, ProgramAnalyzer>();

        public List<(string name,string state)> GetCurrentAnalyzersInfo()
        {
            var ret = new List<(string name, string state)>();
            foreach(var anal in Analyzers)
            {                
                ret.Add((name: anal.Key, state: anal.Value.State.ToString()));
            }
            return ret;
        }

        /// <summary>
        /// Adds a list of <see cref="SystemHealthExternalInterface.SystemEvent"/>s to main queue of the engine.
        /// </summary>
        /// <param name="results">The results.</param>
        public async Task AddToMainQueue(List<SystemEvent> results)
        {
            if (!EngineIsRunning)
            {
                throw new Exception("Engine is not running. Cannot add events to it.");
            }
            await Task.Run(() =>
                           {
                               foreach (var operationResult in results)
                               {
                                   MainEventQueue.Enqueue(operationResult);
                               }
                           });

        }

        /// <summary>
        /// Starts the engine task.
        /// </summary>
        /// <returns></returns>
        private void StartEngineTask()
        {
            Task.Run(() =>
                          {
                              try
                              {                                  
                                  State = State.Running;
                                  AddMessage("Main engine Task started.");
                                  while (State == State.Running)
                                  {
                                      MainLoop();
                                  }
                                  AddMessage($"Shutting down. Running main loop until main queue is empty. {MainEventQueue.Count} events in queue.");
                                  while (MainEventQueue.Count > 0)
                                  {
                                      MainLoop();
                                  }
                                  AddMessage("Main queue emptied. Shutting down analyzers");
                                  foreach (var analyzer in Analyzers)
                                  {
                                      AddMessage($"Stopping analyzer for {analyzer.Key}");
                                      analyzer.Value.StopAnalyzer();
                                  }
                              }
                              catch (Exception ex)
                              {
                                  State = State.Stopped;
                                  var msg = new AlarmMessage(AlarmLevel.Medium, AppDomain.CurrentDomain.FriendlyName, $"Exception in {nameof(AnalyzerEngine)}.{nameof(StartEngineTask)}. Engine is down. Engine will try to restart.", ex.Message);
                                  AlarmMessageManager.RaiseAlarmAsync(msg).Wait();
                              }
                          });


        }

        private void MainLoop()
        {
            if (MainEventQueue.TryDequeue(out SystemEvent fromQ))
            {
                if (Analyzers.ContainsKey(fromQ.AppInfo.ApplicationName))
                {
                    ProgramAnalyzer analyzer;
                    var tryAmount = 0;
                    const int tryUntil = 1000000;
                    while (!Analyzers.TryGetValue(fromQ.AppInfo.ApplicationName, out analyzer))
                    {
                        //We don't want to get stuck here, so only try a limited large number of times.
                        if (tryAmount++ > tryUntil)
                        {
                            break;
                        }
                    }

                    if (analyzer == null)
                    {
                        AddMessage($"{nameof(analyzer)} is null. Tried to get from {nameof(Analyzers)} {tryUntil} times.");
                    }
                    else
                    {
                        if (analyzer.State == State.Stopped)
                        {
                            AddMessage($"{analyzer.ProgramName} analyzer not running. Starting.");
                            analyzer.StartAnalyzerTask();
                        }
                        analyzer.AddEvent(fromQ);
                        AddMessage($"Event added from {fromQ.AppInfo.ApplicationName} to {nameof(Analyzers)}.");
                    }
                }
                else
                {
                    AddMessage($"No analyzer for {fromQ.AppInfo.ApplicationName} in {nameof(Analyzers)}. Trying to add a blank one with no rules.");
                    var analyser = new ProgramAnalyzer(AlarmMessageManager) { ProgramName = fromQ.AppInfo.ApplicationName };
                    if (Analyzers.TryAdd(analyser.ProgramName, analyser))
                    {
                        AddMessage($"Added blank analyzer for {fromQ.AppInfo.ApplicationName} in {nameof(Analyzers)}.");
                        analyser.StartAnalyzerTask();
                        analyser.AddEvent(fromQ);
                    }
                    else
                    {
                        AddMessage($"Failed to add blank analyzer for {fromQ.AppInfo.ApplicationName} in {nameof(Analyzers)}.");
                    }
                }
            }
        }

        public void ReloadRulesForAnalyzer(string analyzerProgramName)
        {
            var analyzer = Analyzers.FirstOrDefault(a => a.Key == analyzerProgramName).Value;
            AddMessage($"Reloading rules for {analyzerProgramName}. Stopping Analyzer.");
            analyzer.StopAnalyzer();
            while(analyzer.State != State.Stopped)
            {
                //wait for stop
            }
            analyzer.UnloadAllRules();
            AddMessage($"{analyzerProgramName} analyzer stopped and rules unloaded.");
            var rules = RuleStorage.GetRulesForApplication(analyzerProgramName);                        
            AddRulesToAnalyzer(rules);
            AddMessage($"{analyzer.ProgramName} analyzer starting.");
            analyzer.StartAnalyzerTask();
        }        

        private void AddRulesToAnalyzer(List<AnalyzeRule> rules)
        {
            foreach (var rule in rules)
            {                
                GetOrCreateAnalyzer(rule.ProgramName).AddOrReplaceRule(rule);             
                AddMessage($"Rule {rule.RuleName} added to {nameof(ProgramAnalyzer)} for {rule.ProgramName}. Rule applies to operation: {(string.IsNullOrEmpty(rule.OperationName) ? "All operations" : rule.OperationName)}.");
            }
        }

        private ProgramAnalyzer GetOrCreateAnalyzer(string programName)
        {
           return Analyzers.GetOrAdd(programName, new ProgramAnalyzer(AlarmMessageManager));
        }


        /// <summary>
        /// Holds the <see cref="AnalyzeRule"/>s to analyse one program. Starts its own task to run analyses.
        /// </summary>
        private class ProgramAnalyzer : ITimeBetweenOperationsObserver
        {
            public ProgramAnalyzer(AlarmMessageManager alarmMessageManager)
            {
                AlarmMessageManager = alarmMessageManager;                
                foreach (var rule in Rules.Where(x => x.Value is TimeBetweenOperations))
                {
                    var realRule = (TimeBetweenOperations)rule.Value;
                    realRule.AttachObserver(this);
                }
            }
            public State State { get; private set; } = State.Stopped;
            public string ProgramName { get; set; }
            private AlarmMessageManager AlarmMessageManager { get; }            
            private ConcurrentDictionary<string, AnalyzeRule> Rules { get; } = new ConcurrentDictionary<string, AnalyzeRule>();
            private ConcurrentQueue<SystemEvent> EventQueue { get; } = new ConcurrentQueue<SystemEvent>();
            public void AddEvent(SystemEvent result)
            {
                EventQueue.Enqueue(result);
            }

            public void StopAnalyzer()
            {
                State = State.ShuttingDown;
            }

            public void StartAnalyzerTask()
            {
                Task.Run(() =>
                               {
                                   try                                   
                                   {
                                       State = State.Running;
                                       while (State == State.Running)
                                       {
                                           MainLoop();
                                       }
                                       while(EventQueue.Count > 0)
                                       {
                                           MainLoop();
                                       }
                                       State = State.Stopped;
                                   }
                                   catch (Exception ex)
                                   {
                                       State = State.Stopped;
                                       var msg = new AlarmMessage(AlarmLevel.Medium, AppDomain.CurrentDomain.FriendlyName, $"Exception in {nameof(ProgramAnalyzer)}.{nameof(StartAnalyzerTask)} for {ProgramName}.", ex.InnerException?.Message ?? ex.Message);
                                       AlarmMessageManager.RaiseAlarm(msg);
                                   }
                               }
                    );
            }

            private void MainLoop()
            {
                if (EventQueue.TryDequeue(out SystemEvent fromQ))
                {
                    Parallel.ForEach(Rules.Where(r => string.IsNullOrEmpty(r.Value.OperationName) || r.Value.OperationName.Equals(fromQ.OperationName)), rule =>
                    {
                        if (rule.Value.AddAndCheckIfTriggered(fromQ))
                        {
                            var msg = new AlarmMessage(rule.Value.AlarmLevel, fromQ.AppInfo.ApplicationName, $"Rule {rule.Value.RuleName} triggered. Message: {rule.Value.AlarmMessage}", fromQ.CaughtException.Message, fromQ.ID);
                            AlarmMessageManager.RaiseAlarm(msg);
#if DEBUG
                            Debug.WriteLine($"ALARM! {rule.Value.AlarmLevel} level. From {fromQ.AppInfo.ApplicationName}. Message: {rule.Value.AlarmMessage}");
#endif
                        }
                    });
                }

            }


            public void AddOrReplaceRule(AnalyzeRule rule)
            {
                if (string.IsNullOrWhiteSpace(ProgramName))
                {
                    ProgramName = rule.ProgramName;
                }
                if (ProgramName != rule.ProgramName)
                {
                    throw new ArgumentException($"This instance of {nameof(ProgramAnalyzer)} is analyzing {ProgramName}. Can not add ruleset for {rule.ProgramName}.");
                }
                Rules.AddOrUpdate(rule.RuleName, rule, (key, oldValue) => rule);
            }

            public void UnloadAllRules()
            {
                Rules.Clear();
            }

            public void RuleTriggeredByTimeout(TimeBetweenOperations rule)
            {
                var msg = new AlarmMessage(rule.AlarmLevel, rule.ProgramName, $"Rule {rule.RuleName} triggered. Message: {rule.AlarmMessage}");
                AlarmMessageManager.RaiseAlarm(msg);
#if DEBUG
                Debug.WriteLine($"ALARM! {rule.AlarmLevel} level. From {rule.ProgramName}. Message: {rule.AlarmMessage}");
#endif

            }
        }
    }
}
