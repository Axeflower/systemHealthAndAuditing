/****************************************************************************************
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
using System.Threading;
using System.Threading.Tasks;
using SystemHealthExternalInterface;
using HealthAndAuditShared.Observers;


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

        public delegate void StateChanged(State state);
        public event StateChanged OnStateChanged;

        public delegate void ReportException(string message, Exception exception);
        public event ReportException OnReportException;

        private void HandleAnalyzerException(string message, Exception exception)
        {
            OnReportException?.Invoke(message,exception);
        }

        private void ChangeState(State newState)
        {
            State = newState;
            OnStateChanged?.Invoke(State);
        }


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
                analyzer.Value.StartAnalyzer();
            }
            StartEngine();
        }
        /// <summary>
        /// Stops the engine safely. Letting all current operations complete but will not allow the engine to start any now tasks.
        /// </summary>
        public void StopEngine()
        {
            Task.Run(() =>
            {
                AddMessage("Initiating engine shutdown.");
                ChangeState(State.ShuttingDown);
                AddMessage("Waiting for all analyzers to finish.");
                var timer = new Stopwatch();
                timer.Start();
                while (Analyzers.Any(a => a.Value.State != State.Stopped) && timer.ElapsedMilliseconds < 30000)
                {
                    AddMessage($"{Analyzers.Count(a => a.Value.State != State.Stopped)} analyzers not stopped. Waited {timer.ElapsedMilliseconds} ms.");
                    Task.Delay(1000).Wait();
                }
                AddMessage("Shutdown complete.");
                ChangeState(State.Stopped);
            });
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

        public delegate void NewAnalyzerInfo(string name, string info);
        public event NewAnalyzerInfo OnNewAnalyzerInfo;
        private void AnalyzerChangeState(string name, string info)
        {            
            OnNewAnalyzerInfo?.Invoke(name, info);
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
        private void StartEngine()
        {
            var engineThread = new Thread(() =>
                          {
                              try
                              {
                                  ChangeState(State.Running);
                                  AddMessage("Main engine thread started.");
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
                                  ChangeState(State.Stopped);
                                  var message = $"Exception in {nameof(AnalyzerEngine)}.{nameof(StartEngine)}. Engine is down. Engine will try to restart.";
                                  OnReportException?.Invoke(message,ex);
                                  var alarmMessage = new AlarmMessage(AlarmLevel.Medium, AppDomain.CurrentDomain.FriendlyName, message, ex.Message);
                                  AlarmMessageManager.RaiseAlarm(alarmMessage);
                              }
                          });

            engineThread.Priority = ThreadPriority.Highest;
            engineThread.Name = nameof(engineThread);
            engineThread.Start();
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
                            analyzer.StartAnalyzer();
                        }
                        analyzer.AddEvent(fromQ);
                        AddMessage($"{fromQ.Result} event {fromQ.OperationName} added from {fromQ.AppInfo.ApplicationName}.");
                    }
                }
                else
                {
                    AddMessage($"No analyzer for {fromQ.AppInfo.ApplicationName} in {nameof(Analyzers)}. Trying to add a blank one with no rules.");
                    var analyser = new ProgramAnalyzer(AlarmMessageManager) { ProgramName = fromQ.AppInfo.ApplicationName };
                    if (Analyzers.TryAdd(analyser.ProgramName, analyser))
                    {
                        analyser.OnAnalyzerInfo += AnalyzerChangeState;
                        analyser.OnReportException += HandleAnalyzerException;
                        AddMessage($"Added blank analyzer for {fromQ.AppInfo.ApplicationName} in {nameof(Analyzers)}.");
                        analyser.StartAnalyzer();
                        analyser.AddEvent(fromQ);
                    }
                    else
                    {
                        AddMessage($"Failed to add blank analyzer for {fromQ.AppInfo.ApplicationName} in {nameof(Analyzers)}.");
                    }
                }
            }
            else
            {
                Thread.Sleep(2000);
            }
        }

        public void ReloadRulesForAnalyzer(string analyzerProgramName)
        {
            Task.Run(() =>
            {
                var analyzer = Analyzers.FirstOrDefault(a => a.Key == analyzerProgramName).Value;
                AddMessage($"Reloading rules for {analyzerProgramName}. Stopping Analyzer.");
                analyzer.StopAnalyzer();
                while (analyzer.State != State.Stopped)
                {
                    //wait for stop
                }
                analyzer.UnloadAllRules();
                AddMessage($"{analyzerProgramName} analyzer stopped and rules unloaded.");
                var rules = RuleStorage.GetRulesForApplication(analyzerProgramName);
                AddRulesToAnalyzer(rules);
                AddMessage($"{analyzer.ProgramName} analyzer starting.");
                analyzer.StartAnalyzer();
            });
        }

        public List<string> GetRulesLoadedInAnalyzer(string analyzerProgramName)
        {
            if (Analyzers.ContainsKey(analyzerProgramName))
            {
                var analyzer = Analyzers[analyzerProgramName];
                return analyzer.Rules.Select(rule => rule.Key).ToList();
            }
            return new List<string>();
        }
        public List<AnalyzerInstanceInfo> GetCurrentAnalyzersInfo()
        {
            var ret = new List<AnalyzerInstanceInfo>();
            foreach (var anal in Analyzers)
            {
                ret.Add(new AnalyzerInstanceInfo { Name = anal.Key, State = anal.Value.State.ToString(), EventsInQueue= anal.Value.NumberOfEventsInQueue, NumberOfRulesLoaded = anal.Value.Rules.Count});
            }
            return ret;
        }

        public AnalyzerInstanceInfo GetInfoForAnalyzer(string analyzerProgramName)
        {
            if (Analyzers.ContainsKey(analyzerProgramName))
            {
                var analyzer = Analyzers[analyzerProgramName];
                return new AnalyzerInstanceInfo {EventsInQueue = analyzer.NumberOfEventsInQueue,Name = analyzer.ProgramName,NumberOfRulesLoaded = analyzer.Rules.Count,State = analyzer.State.ToString()};
            }
            return new AnalyzerInstanceInfo();
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
            var analyzer = Analyzers.GetOrAdd(programName, new ProgramAnalyzer(AlarmMessageManager));
            analyzer.OnAnalyzerInfo -= AnalyzerChangeState;
            analyzer.OnAnalyzerInfo += AnalyzerChangeState;
            analyzer.OnReportException -= HandleAnalyzerException;
            analyzer.OnReportException += HandleAnalyzerException;
            return analyzer;
        }

        public class AnalyzerInstanceInfo
        {
            public string Name { get; set; }
            public string State { get; set; }
            public int EventsInQueue { get; set; }
            public int NumberOfRulesLoaded { get; set; }
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

            public delegate void AnalyzerInfo(string name, string info);
            public event AnalyzerInfo OnAnalyzerInfo;
            public delegate void ReportException(string message, Exception exception);
            public event ReportException OnReportException;
            private void AnalyzerChangeState(State state)
            {
                State = state;
                var info = State + ". " + Rules.Count + " rules loaded." + NumberOfEventsInQueue + " events in queue.";
                OnAnalyzerInfo?.Invoke(ProgramName, info);
            }

            public State State { get; private set; } = State.Stopped;
            public string ProgramName { get; set; }
            private AlarmMessageManager AlarmMessageManager { get; }           
            internal ConcurrentDictionary<string, AnalyzeRule> Rules { get; } = new ConcurrentDictionary<string, AnalyzeRule>();
            internal int NumberOfEventsInQueue => EventQueue.Count;
            private ConcurrentQueue<SystemEvent> EventQueue { get; } = new ConcurrentQueue<SystemEvent>();
            public void AddEvent(SystemEvent result)
            {
                EventQueue.Enqueue(result);
            }

            public void StopAnalyzer()
            {
                AnalyzerChangeState(State.ShuttingDown);
            }

            public void StartAnalyzer()
            {

                var analyzerThread = new Thread(() =>
                               {
                                   try                                   
                                   {
                                       AnalyzerChangeState(State.Running);
                                       while (State == State.Running)
                                       {
                                           MainLoop();
                                       }
                                       while(EventQueue.Count > 0)
                                       {
                                           MainLoop();
                                       }
                                       AnalyzerChangeState(State.Stopped);
                                   }
                                   catch (Exception ex)
                                   {
                                       try
                                       {
                                           AnalyzerChangeState(State.Stopped);
                                           var message = $"Exception in {nameof(ProgramAnalyzer)}.{nameof(StartAnalyzer)} for {ProgramName}.";
                                           OnReportException?.Invoke(message, ex);
                                           var alarmMessage = new AlarmMessage(AlarmLevel.Medium, AppDomain.CurrentDomain.FriendlyName, message, ex.InnerException?.Message ?? ex.Message);
                                           AlarmMessageManager.RaiseAlarm(alarmMessage);
                                       }
                                       catch(Exception ex2)
                                       {                                           
                                           OnReportException?.Invoke("Exception in Exception handling", ex2);
                                       }
                                   }
                               }
                    );
                analyzerThread.Priority = ThreadPriority.Highest;
                analyzerThread.Name = nameof(analyzerThread) + "_" + ProgramName;
                analyzerThread.Start();
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
                else
                {
                    AnalyzerChangeState(State.Stopped);
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
