using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HealthAndAuditShared;
using Microsoft.ServiceBus;
using static System.Console;

namespace AnalyzerEngineConsole
{
    internal class Program
    {
        public static FileLogger Logger { get; set; } = new FileLogger(maxIterations: 10, async: true);
        public static FileLogger ErrorLogger { get; set; } = new FileLogger(filePrefix: "ErrorLog_", maxIterations: 10);
        public static AnalyzerEngine Engine { get; set; } = new AnalyzerEngine();

        public static List<Timer> Timers { get; } = new List<Timer>();

        public static bool RunRestartLoop { get; set; } = true;
        public static string EventHubName { get; private set; }
        private const int MF_BYCOMMAND = 0x00000000;
        public const int SC_CLOSE = 0xF060;

        [DllImport("user32.dll")]
        public static extern int DeleteMenu(IntPtr hMenu, int nPosition, int wFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();

        private static void Main()
        {
            DeleteMenu(GetSystemMenu(GetConsoleWindow(), false), SC_CLOSE, MF_BYCOMMAND);
            var storageConnection = ConfigurationManager.AppSettings["AzureStorageConnectionString"];
            ServiceBusConnectionStringBuilder builder = new ServiceBusConnectionStringBuilder(ConfigurationManager.AppSettings["Microsoft.ServiceBus.ConnectionString.Listen"]);
            EventHubName = ConfigurationManager.AppSettings["EventHubName"];
            var hubProcessor = new EventHubProcessor(builder.ToString(), EventHubName);
            AnalyzerEngineProgram engineProgram = new AnalyzerEngineProgram(Engine, Logger, ErrorLogger, new StatusSnapShotGenerator(ConfigurationManager.AppSettings["jsonPath"], ErrorLogger), hubProcessor);


            var alarmQueueConnS = ConfigurationManager.AppSettings["ServiceBus.Queue.Connectionstring"];
            var alarmQueueName = ConfigurationManager.AppSettings["ServiceBus.Queue.Name"];

            var alarmQueue = new ServiceBusConnection<AlarmMessage>(alarmQueueConnS, alarmQueueName);
            var alarmManger = new AlarmMessageManager(alarmQueue);
            var ruleStorage = new DocumentDBRuleStorage(ConfigurationManager.AppSettings["DocDBEndPointUrl"], ConfigurationManager.AppSettings["AuthorizationKey"], ConfigurationManager.AppSettings["RuleDatabaseId"], ConfigurationManager.AppSettings["RuleCollectionId"]);

            var eventStore = new SQLEventStore(Logger, ConfigurationManager.AppSettings["sqleventstoreConnection"]);
                        
            var restartInput = new RestartInput() { RuleStorage = ruleStorage, AlarmMessageManager = alarmManger, EngineProgram = engineProgram };
            var engineRestartTimer = new Timer(RestartLoop, restartInput, 0, 5000);

            Timers.Add(engineRestartTimer);

            var eventProcessorThread = new Thread(() =>
            {
                var recTask = hubProcessor.StartReceiver<EventProcessor>(storageConnection);
                EventProcessor.Init(Engine, Logger, ErrorLogger, eventStore);
                recTask.Wait();
            });
            eventProcessorThread.Name = nameof(eventProcessorThread);

            var gui = new GuiHandler(engineProgram);
            var guiThread = new Thread(gui.Run);
            guiThread.IsBackground = true;
            guiThread.Start();
            guiThread.Name = nameof(guiThread);
            eventProcessorThread.Priority = ThreadPriority.AboveNormal;
            eventProcessorThread.Start();            
        }

        private class RestartInput
        {
            public IRuleStorage RuleStorage { get; set; }
            public AlarmMessageManager AlarmMessageManager { get; set; }
            public int EngineStartCounter { get; set; }
            public int MaxEngineRestarts { get; set; } = 10;
            public AnalyzerEngineProgram EngineProgram { get; set; }
            public bool LoopIsRunning { get; set; }
        }

        private static void RestartLoop(object state)
        {
            var inp = state as RestartInput;
            if (Engine.State == State.ShuttingDown)
            {
                return;
            }
            if (inp.LoopIsRunning)
            {
                return;
            }
            inp.LoopIsRunning = true;
            if (Engine.State == State.Stopped)
            {
                Engine.StartEngine(inp.RuleStorage, inp.AlarmMessageManager);
                if (inp.MaxEngineRestarts <= inp.EngineStartCounter++)
                {
                    var message = $"AnalyzerEngine main task has been restarted {inp.EngineStartCounter - 1} times. Engine is down and can not recover! Resetting start counter.";
                    Logger.AddRow(message);
                    inp.EngineProgram.MesseageOutputQueue.Enqueue(message);
                    var alarm = new AlarmMessage(AlarmLevel.High, AppDomain.CurrentDomain.FriendlyName, message);
                    inp.AlarmMessageManager.RaiseAlarm(alarm);
                    inp.EngineStartCounter = 0;
                }
                var timer = new Stopwatch();
                timer.Start();
                while (!Engine.EngineIsRunning && timer.ElapsedMilliseconds < 20000)
                {
                    inp.EngineProgram.MesseageOutputQueue.Enqueue("Awaiting engine start. Waited " + timer.ElapsedMilliseconds + " ms");
                    Task.Delay(1000).Wait();
                }
                timer.Reset();
            }
            inp.LoopIsRunning = false;
        }
    }


    public class AnalyzerEngineProgram
    {
        public class State
        {
            public string EngineState { get; set; }
            public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
            public string EventHubState { get; set; }
            public DateTime LastSnapshot { get; set; } = DateTime.MinValue;

            public override string ToString()
            {
                var ret = new StringBuilder();
                foreach (var propertyInfo in GetType().GetProperties())
                {
                    ret.AppendLine(propertyInfo.Name + ": " + propertyInfo.GetValue(this));
                }
                return ret.ToString();
            }
        }

        public StatusSnapShotGenerator SnapShotGenerator { get; set; }

        public ConcurrentQueue<string> MesseageOutputQueue { get; set; } = new ConcurrentQueue<string>();
        private FileLogger Logger { get; }
        private FileLogger ErrorLogger { get; }
        private AnalyzerEngine Engine { get; }
        private EventHubProcessor EventHubProcessor { get; }
        private State CurrentState { get; } = new State();
        public bool Running { get; private set; } = true;

        public enum Commands
        {
            help,
            reloadrulesforanal,
            exit,
            state,
            showloadedanal,
            showrulesforanal,
            restart
        }
        public ConcurrentQueue<string> OutputQueue { get; } = new ConcurrentQueue<string>();
        public void InputCommand(string command)
        {
            var divided = command.Split(' ');
            if (Enum.TryParse(divided[0], true, out Commands com))
            {
                HandleCommand(com, divided);
            }
            else
            {
                OutputQueue.Enqueue("unknown command " + divided[0]);
            }
        }
        private void HandleCommand(Commands command, string[] args)
        {
            OutputQueue.Enqueue("Command " + command + " recieved.");
            switch (command)
            {
                case Commands.help:

                    OutputQueue.Enqueue("Available commands:");
                    foreach (var avCom in Enum.GetValues(typeof(Commands)))
                    {
                        OutputQueue.Enqueue(avCom.ToString());
                    }
                    break;
                case Commands.exit:
                    Program.RunRestartLoop = false;
                    Engine.StopEngine();
                    Running = false;
                    EventHubProcessor.StopReceiver();
                    SnapShotGenerator.Stop();
                    OutputQueue.Enqueue("Command sent to engine. See log for info.");
                    break;
                case Commands.reloadrulesforanal:
                    if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
                    {
                        OutputQueue.Enqueue("missing analyzer name");
                        break;
                    }
                    Engine.ReloadRulesForAnalyzer(args[1]);
                    OutputQueue.Enqueue("Command sent to engine. See log for info.");
                    break;
                case Commands.showrulesforanal:
                    if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
                    {
                        OutputQueue.Enqueue("missing analyzer name");
                        break;
                    }
                    var rules = Engine.GetRulesLoadedInAnalyzer(args[1]);
                    var loaded = rules.Aggregate("", (current, rule) => current + rule + Environment.NewLine);
                    OutputQueue.Enqueue(loaded);
                    break;
                case Commands.state:
                    OutputQueue.Enqueue(CurrentState.ToString());
                    break;
                case Commands.showloadedanal:
                    OutputQueue.Enqueue(Environment.NewLine);
                    OutputQueue.Enqueue(string.Join(Environment.NewLine, Engine.GetCurrentAnalyzersInfo().Select(item => $"{item.Name}\t\t{item.State}\tin queue {item.EventsInQueue}.\tLoaded rules {item.NumberOfRulesLoaded}.")));
                    break;
                case Commands.restart:
                    Task.Run(() =>
                    {
                        Thread.CurrentThread.IsBackground = true;
                        SnapShotGenerator.Reset();
                        Engine.StopEngine();
                    });
                    OutputQueue.Enqueue("Command sent to engine. See log for info.");
                    break;
            }
        }

        public AnalyzerEngineProgram(AnalyzerEngine engine, FileLogger logger, FileLogger errorLogger, StatusSnapShotGenerator snapGen, EventHubProcessor eventHubProcessor)
        {
            Engine = engine;
            Logger = logger;
            ErrorLogger = errorLogger;
            SnapShotGenerator = snapGen;
            EventHubProcessor = eventHubProcessor;
            SnapShotGenerator.StartGenerator();
            Engine.OnStateChanged += HandleEngineStateChange;
            Engine.OnReportException += HandleEngineException;
            EventProcessor.OnUpdatedInfo += HandleEventProcessorInfo;

            var toMessageLoop = new ToMessageLoop() { Engine = Engine, Logger = Logger, SnapShotGenerator = SnapShotGenerator, MesseageOutputQueue = MesseageOutputQueue };
            var messageTimer = new Timer(MessageLoop, toMessageLoop, 0, 1000);
            Program.Timers.Add(messageTimer);

            var snapshotUpdateThread = new Thread(() =>
            {
                while (true)
                {
                    UpdateSnapshotAnalyzerInfo();
                    CurrentState.LastSnapshot = SnapShotGenerator.LastFileGeneratedTime;
                    Thread.Sleep(4000);
                }
            });
            snapshotUpdateThread.Name = nameof(snapshotUpdateThread);
            snapshotUpdateThread.Priority = ThreadPriority.BelowNormal;
            snapshotUpdateThread.IsBackground = true;
            snapshotUpdateThread.Start();
        }

        private class ToMessageLoop
        {
            public StatusSnapShotGenerator SnapShotGenerator { get; set; }
            public FileLogger Logger { get; set; }
            public AnalyzerEngine Engine { get; set; }
            public ConcurrentQueue<string> MesseageOutputQueue { get; set; }
            public bool LoopIsRunning { get; set; }
        }

        private static void MessageLoop(object state)
        {
            var fromOutSide = state as ToMessageLoop;
            if (fromOutSide.LoopIsRunning)
            {
                return;
            }
            fromOutSide.LoopIsRunning = true;
            var messageAggregator = new MessageAggregator<string>();
            for (var i = 0; i < 50; ++i)
            {
                if (fromOutSide.Engine.EngineMessages.TryDequeue(out var msg))
                {
                    messageAggregator.AddMessage(msg, msg.Message.GenerateMessageIdentifierFromString());
                }
            }
            foreach (var messageTracker in messageAggregator.Collection)
            {
                fromOutSide.MesseageOutputQueue.Enqueue($"{messageTracker.Value.Message} | {messageTracker.Value.AmountCounter} times from {messageTracker.Value.FirstOccurrence} to {messageTracker.Value.LastOccurrence}");
            }
            for (int i = 1; i <= 140; ++i)
            {
                if (fromOutSide.MesseageOutputQueue.TryDequeue(out string message))
                {
                    fromOutSide.Logger.AddRow(message);
                    fromOutSide.SnapShotGenerator.AddMessageToSnapShot(DateTime.UtcNow, message);
                }
            }
            fromOutSide.LoopIsRunning = false;
        }

        private void HandleEngineException(string message, Exception exception)
        {
            ErrorLogger.AddRow(message);
            ErrorLogger.AddRow(exception.ToString());
        }

        private void HandleEngineStateChange(HealthAndAuditShared.State state)
        {
            CurrentState.EngineState = $"AnalyzerEngine is: {state}";
        }

        private void HandleEventProcessorInfo(ConcurrentDictionary<string, string> info)
        {
            var newText = "";
            foreach (var entry in info)
            {
                newText += $"{entry.Key}: {entry.Value}{Environment.NewLine}";
            }
            CurrentState.EventHubState = newText;
        }

        private void UpdateSnapshotAnalyzerInfo()
        {
            var analyzerlist = Engine.GetCurrentAnalyzersInfo();
            foreach (var anal in analyzerlist)
            {
                SnapShotGenerator.AddAnalyzerInfoToSnapShot(anal);
            }
        }

    }
    public class GuiHandler
    {
        public GuiHandler(AnalyzerEngineProgram realProgram)
        {
            RealProgram = realProgram;
        }
        private FileLogger OutputLog { get; } = new FileLogger(filePrefix: "OutputLog_");
        private AnalyzerEngineProgram RealProgram { get; }

        public void Run()
        {
            Thread.Sleep(1000);
            while (true)
            {
                while (RealProgram.OutputQueue.TryDequeue(out var fromQueue))
                {
                    OutputLog.AddRow(fromQueue);
                    WriteLine(fromQueue);
                }
                if (RealProgram.Running)
                {
                    WriteLine("Input command (help to see available commands):");
                    RealProgram.InputCommand(ReadLine());
                }
            }
        }
    }
}
