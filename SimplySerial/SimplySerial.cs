﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Management;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace SimplySerial
{
    class SimplySerial
    {
        const string version = "0.8.0-alpha.2";

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        public static extern uint GetLastError();

        static readonly string appFolder = AppDomain.CurrentDomain.BaseDirectory;
        static BoardData boardData;

        static List<ComPort> availablePorts = new List<ComPort>();
        static SerialPort serialPort;

        // default comspec values and application settings set here will be overridden by values passed through command-line arguments
        static bool Quiet = false;
        static AutoConnect autoConnect = AutoConnect.ONE;
        static ComPort port = new ComPort();
        static int baud = -1;
        static Parity parity = Parity.None;
        static int dataBits = 8;
        static StopBits stopBits = StopBits.One;
        const Handshake handshake = Handshake.None; // we don't support any handshaking
        static bool logging = false;
        static FileMode logMode = FileMode.Create;
        static string logFile = string.Empty;
        static string logData = string.Empty;
        static int bufferSize = 102400;
        static DateTime lastFlush = DateTime.Now;
        static bool forceNewline = false;

        // dictionary of "special" keys with the corresponding string to send out when they are pressed
        static Dictionary<ConsoleKey, String> specialKeys = new Dictionary<ConsoleKey, String>
        {
            { ConsoleKey.UpArrow, "\x1B[A" },
            { ConsoleKey.DownArrow, "\x1B[B" },
            { ConsoleKey.RightArrow, "\x1B[C" },
            { ConsoleKey.LeftArrow, "\x1B[D" },
            { ConsoleKey.Home, "\x1B[H" },
            { ConsoleKey.End, "\x1B[F" },
            { ConsoleKey.Insert, "\x1B[2~" },
            { ConsoleKey.Delete, "\x1B[3~" },
            { ConsoleKey.PageUp, "\x1B[5~" },
            { ConsoleKey.PageDown, "\x1B[6~" }
        };

        static void Main(string[] args)
        {
            // attempt to enable virtual terminal escape sequence processing
            try
            {
                var iStdOut = GetStdHandle(STD_OUTPUT_HANDLE);
                GetConsoleMode(iStdOut, out uint outConsoleMode);
                outConsoleMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                SetConsoleMode(iStdOut, outConsoleMode);
            }
            catch
            {
                // if the above fails, it doesn't really matter - it just means escape sequences won't process nicely
            }

            // load and parse data in boards.json
            LoadBoards();

            // process all command-line arguments
            ProcessArguments(args);

            // verify log-related settings
            if (logging)
            {
                try
                { 
                    FileStream stream = new FileStream(logFile, logMode, FileAccess.Write);
                    using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.WriteLine($"\n----- LOGGING STARTED ({DateTime.Now}) ------------------------------------");
                    }
                }
                catch (Exception e)
                {
                    logging = false;
                    ExitProgram($"* Error accessing log file '{logFile}'\n  > {e.GetType()}: {e.Message}", exitCode: -1);
                }
            }
            
            // set up keyboard input for program control / relay to serial port
            ConsoleKeyInfo keyInfo = new ConsoleKeyInfo();
            Console.TreatControlCAsInput = true; // we need to use CTRL-C to activate the REPL in CircuitPython, so it can't be used to exit the application

            // this is where data read from the serial port will be temporarily stored
            string received = string.Empty;

            // Initialization complete, display greeting
            Console.Clear();
            string cmdline_args = string.Join(" ", args);
            Output(string.Format("<<< SimplySerial v{0} started with{1} parameters{2} >>>",
                version,
                (string.IsNullOrWhiteSpace(cmdline_args)) ? "out" : "",
                (string.IsNullOrWhiteSpace(cmdline_args)) ? "" : " [ " + cmdline_args + " ]"
                ), flush: true);

            // create a list of available ports
            availablePorts = (SimplySerial.GetSerialPorts()).OrderBy(p => p.num).ToList();
            ListAvailablePorts();
            //Output($"<<< Available Comm Ports: {string.Join(",", availablePorts.Select(o=>o.name))} >>>");


            //main loop - keep this up until user presses CTRL-X or an exception takes us down
            do
            {
                // first things first, check for (and respect) a request to exit the program via CTRL-X
                if (Console.KeyAvailable)
                {
                    keyInfo = Console.ReadKey(intercept: true);
                    if ((keyInfo.Key == ConsoleKey.X) && (keyInfo.Modifiers == ConsoleModifiers.Control))
                    {
                        Output("\n<<< SimplySerial session terminated via CTRL-X >>>");
                        ExitProgram(silent: true);
                    }
                }

                // if no port was specified/selected, pick one automatically
                if (string.IsNullOrEmpty(port.name))
                {
                    // if there are com ports available, pick one
                    if (availablePorts.Count() >= 1)
                    {

                        
                        // try the first available port that we assume is running CircuitPython
                        SimplySerial.port = availablePorts.Find(p => p.isCircuitPython == true);

                        // if that doesn't work out, just default to the first available COM port
                        if (SimplySerial.port == null)
                            SimplySerial.port = availablePorts[0];
                    }

                    // if there are no com ports available, exit or try again depending on autoconnect setting 
                    else
                    {
                        if (autoConnect == AutoConnect.NONE)
                        {
                            ExitProgram("No COM ports detected.", exitCode: -1);
                        }
                        else
                        {
                            Output("<<< No COM ports not detected, Retrying.  Use CTRL-X to cancel. >>>");
                            Thread.Sleep(2000);
                            continue;
                        }
                    }
                }

                // if a specific port has been selected, try to match it with one that actually exists
                else
                {
                    bool portMatched = false;

                    foreach (ComPort p in availablePorts)
                    {
                        if (p.name == port.name)
                        {
                            portMatched = true;
                            port = p;
                            break;
                        }
                    }

                    // if the specified port is not available, exit or try again depending on autoconnect setting
                    if (!portMatched)
                    {
                        if (autoConnect == AutoConnect.NONE)
                        {
                            ExitProgram(("Invalid port specified <" + port.name + ">"), exitCode: -1);
                        }
                        else
                        {
                            Output("<<< Invalid port specified <" + port.name + ">, Retrying.  Use CTRL-X to cancel. >>>");
                            Thread.Sleep(2000);
                            continue;
                        }
                    }
                }

                // if we get this far, it should be safe to set up the specified/selected serial port
                Output("<<< Trying to connect on " + port.name + ",  Use CTRL-X to cancel. >>>");
                
                serialPort = new SerialPort(port.name)
                {
                    ReadTimeout = 1, // minimal timeout - we don't want to wait forever for data that may not be coming!
                    WriteTimeout = 250, // small delay - if we go too small on this it causes System.IO semaphore timeout exceptions
                    DtrEnable = true, // without this we don't ever receive any data
                    RtsEnable = true // without this we don't ever receive any data
                };

                // attempt to set the baud rate, fail if the specified value is not supported by the hardware
                try
                {
                    if (baud < 0)
                    {
                        if (port.isCircuitPython)
                            baud = 115200;
                        else
                            baud = 9600;
                    }
                    
                    serialPort.BaudRate = baud;
                }
                catch (ArgumentOutOfRangeException)
                {
                    ExitProgram(("The specified baud rate (" + baud + ") is not supported."), exitCode: -2);
                }

                // set other port parameters (which have already been validated)
                serialPort.Handshake = handshake;
                serialPort.Parity = parity;
                serialPort.DataBits = dataBits;
                serialPort.StopBits = stopBits;

                // attempt to open the serial port, deal with failures
                try
                {
                    serialPort.Open();
                }
                catch (Exception e)
                {
                    // if auto-connect is disabled than any exception should result in program termination
                    if (autoConnect == AutoConnect.NONE)
                    {
                        if (e is UnauthorizedAccessException)
                            ExitProgram((e.GetType() + " occurred while attempting to open " + port.name + ".  Is this port already in use in another application?"), exitCode: -1);
                        else
                            ExitProgram((e.GetType() + " occurred while attempting to open " + port.name + "."), exitCode: -1);
                    }

                    // if auto-connect is enabled, prepare to try again
                    serialPort.Dispose();
                    Thread.Sleep(1000); // putting a delay here to avoid gobbling tons of resources through constant high-speed re-connect attempts
                    continue;
                }

                // if we get this far, clear the screen and send the connection message if not in 'quiet' mode
                // Console.Clear();
                Output(String.Format("--- {0} Connected ---\n" +
                    "Settings  : {1} baud, {2} parity, {3} data bits, {4} stop bit{5}, {6} flow-control, auto-connect {7}.\n" +
                    "Device    : {8} {9}{10}\n{11}" +
                    "---\n\n<<< Use CTRL-X to exit. >>>\n",
                    port.name,
                    baud,
                    (parity == Parity.None) ? "no" : (parity.ToString()).ToLower(),
                    dataBits,
                    (stopBits == StopBits.None) ? "0" : (stopBits == StopBits.One) ? "1" : (stopBits == StopBits.OnePointFive) ? "1.5" : "2", (stopBits == StopBits.One) ? "" : "s",
                    (handshake == Handshake.None) ? "no" : (handshake == Handshake.XOnXOff) ? "software" : (handshake == Handshake.RequestToSend) ? "hardware" : (handshake == Handshake.RequestToSendXOnXOff) ? "HW & SW" : "",
                    (autoConnect == AutoConnect.ONE) ? "on" : (autoConnect == AutoConnect.ANY) ? "any" : "off",
                    port.board.make,
                    port.board.model,
                    (port.isCircuitPython) ? " (CircuitPython-capable)" : "",
                    (logging == true) ? ($"Logfile   : {logFile} (Mode = " + ((logMode == FileMode.Create) ? "OVERWRITE" : "APPEND") + ")\n" ) : ""
                ), flush: true);

                lastFlush = DateTime.Now;
                DateTime start = DateTime.Now;
                TimeSpan timeSinceRX = new TimeSpan();
                TimeSpan timeSinceFlush = new TimeSpan();

                // this is the core functionality - loop while the serial port is open
                while (serialPort.IsOpen)
                {
                    try
                    {
                        // process keypresses for transmission through the serial port
                        if (Console.KeyAvailable)
                        {
                            // determine what key is pressed (including modifiers)
                            keyInfo = Console.ReadKey(intercept: true);

                            // exit the program if CTRL-X was pressed
                            if ((keyInfo.Key == ConsoleKey.X) && (keyInfo.Modifiers == ConsoleModifiers.Control))
                            {
                                Output("\n<<< SimplySerial session terminated via CTRL-X >>>");
                                ExitProgram(silent: true);
                            }

                            // check for keys that require special processing (cursor keys, etc.)
                            else if (specialKeys.ContainsKey(keyInfo.Key))
                                serialPort.Write(specialKeys[keyInfo.Key]);

                            // everything else just gets sent right on through
                            else
                                serialPort.Write(Convert.ToString(keyInfo.KeyChar));
                        }

                        // process data coming in from the serial port
                        received = serialPort.ReadExisting();

                        // if anything was received, process it
                        if (received.Length > 0)
                        {
                            if (forceNewline)
                                received = received.Replace("\r", "\n");

                            // write what was received to console
                            Output(received, force: true, newline: false);
                            start = DateTime.Now;
                        }
                        else
                            Thread.Sleep(1);

                        if (logging)
                        {
                            timeSinceRX = DateTime.Now - start;
                            timeSinceFlush = DateTime.Now - lastFlush;
                            if ((timeSinceRX.TotalSeconds >= 2) || (timeSinceFlush.TotalSeconds >= 10))
                            {
                                if (logData.Length > 0)
                                    Output("", force: true, newline: false, flush: true);
                                start = DateTime.Now;
                                lastFlush = DateTime.Now;
                            }
                        }

                        // if the serial port is unexpectedly closed, throw an exception
                        if (!serialPort.IsOpen)
                            throw new IOException();
                    }
                    catch (Exception e)
                    {
                        if (autoConnect == AutoConnect.NONE)
                            ExitProgram((e.GetType() + " occurred while attempting to read/write to/from " + port.name + "."), exitCode: -1);
                        else
                            Output("\n<<< Communications Interrupted >>>\n");
                        try
                        {
                            serialPort.Dispose();
                        }
                        catch
                        {
                            //nothing to do here, other than prevent execution from stopping if dispose() throws an exception
                        }
#if DEBUG
                        Thread.Sleep(250); // short wait for "interrupted" message in debug
#else
                        Thread.Sleep(1000); // sort-of arbitrary delay - should be long enough to read the "interrupted" message
#endif
                        if (autoConnect == AutoConnect.ANY)
                        {
                            // refresh list of available ports
                            availablePorts = (SimplySerial.GetSerialPorts()).OrderBy(p => p.num).ToList();

                            // TODO: increment port to next available (instead of selecting first available)
                            port.name = String.Empty;
                        }
                        else if (autoConnect == AutoConnect.ONE)
                        {
                            // Reconnect on the same port
                        }
                        break;
                    }
                }
            } while (autoConnect > AutoConnect.NONE);

            // if we get to this point, we should be exiting gracefully
            ExitProgram("<<< SimplySerial session terminated >>>", exitCode: 0);
        }

        /// <summary>
        /// Validates and processes any command-line arguments that were passed in.  Invalid arguments will halt program execution.
        /// </summary>
        /// <param name="args">Command-line parameters</param>
        static void ProcessArguments(string[] args)
        {
            port.name = String.Empty;

            // switch to lower case and remove '/', '--' and '-' from beginning of arguments - we can process correctly without them
            for (int i = 0; i < args.Count(); i++)
                args[i] = args[i].TrimStart('/', '-');

            // sort the parameters so that they get processed in order of priority (i.e. 'quiet' option gets processed before something that would normally result in console output, etc.)
            Array.Sort(args, new ArgumentSorter());

            // iterate through command-line arguments
            foreach (string arg in args)
            {
                // split argument into components based on 'key:value' formatting             
                string[] argument = arg.Split(new [] { ':' }, 2);
                argument[0] = argument[0].ToLower();

                // help
                if (argument[0].StartsWith("h") || argument[0].StartsWith("?"))
                {
                    ShowHelp();
                    ExitProgram(silent: true);
                }

                // version
                if (argument[0].StartsWith("v"))
                {
                    ShowVersion();
                    ExitProgram(silent: true);
                }

                // list available ports
                else if ((argument[0] == "l") || (argument[0].StartsWith("li")))
                {
                    // get a list of all available ports
                    availablePorts = (SimplySerial.GetSerialPorts()).OrderBy(p => p.num).ToList();

                    ListAvailablePorts();

                    ExitProgram(silent: true);
                }

                // quiet (no output to console other than comes in via serial)
                else if (argument[0].StartsWith("q"))
                {
                    SimplySerial.Quiet = true;
                }

                // force linefeeds in place of carriage returns in received data
                else if (argument[0].StartsWith("f"))
                {
                    forceNewline = true;
                }

                // the remainder of possible command-line arguments require two parameters, so let's enforce that now
                else if (argument.Count() < 2)
                {
                    ExitProgram(("Invalid or incomplete argument <" + arg + ">\nTry 'ss.exe help' to see a list of valid arguments"), exitCode: -1);
                }

                // preliminary validate on com port, final validation occurs towards the end of this method 
                else if (argument[0].StartsWith("c"))
                {
                    string newPort = argument[1].ToUpper();

                    if (!argument[1].StartsWith("COM"))
                        if (argument[1].All(char.IsNumber))
                            newPort = "COM" + argument[1];
                    port.name = newPort;
                    autoConnect = AutoConnect.ONE;
                }

                // process baud rate, invalid rates will throw exceptions and get handled elsewhere
                else if (argument[0].StartsWith("b"))
                {
                    baud = Convert.ToInt32(argument[1]);
                }

                // validate parity, terminate on error
                else if (argument[0].StartsWith("p"))
                {
                    argument[1] = argument[1].ToLower();

                    if (argument[1].StartsWith("e"))
                        parity = Parity.Even;
                    else if (argument[1].StartsWith("m"))
                        parity = Parity.Mark;
                    else if (argument[1].StartsWith("n"))
                        parity = Parity.None;
                    else if (argument[1].StartsWith("o"))
                        parity = Parity.Odd;
                    else if (argument[1].StartsWith("s"))
                        parity = Parity.Space;
                    else
                        ExitProgram(("Invalid parity specified <" + argument[1] + ">"), exitCode: -1);
                }

                // validate databits, terminate on error
                else if (argument[0].StartsWith("d"))
                {
                    int newDataBits = Convert.ToInt32(argument[1]);

                    if ((newDataBits > 3) && (newDataBits < 9))
                        dataBits = newDataBits;
                    else
                        ExitProgram(("Invalid data bits specified <" + argument[1] + ">"), exitCode: -1);
                }

                // validate stopbits, terminate on error
                else if (argument[0].StartsWith("s"))
                {
                    if (argument[1] == "0")
                        stopBits = StopBits.None;
                    else if (argument[1] == "1")
                        stopBits = StopBits.One;
                    else if (argument[1] == "1.5")
                        stopBits = StopBits.OnePointFive;
                    else if (argument[1] == "2")
                        stopBits = StopBits.Two;
                    else
                        ExitProgram(("Invalid stop bits specified <" + argument[1] + ">"), exitCode: -1);
                }

                // validate auto connect, terminate on error
                else if (argument[0].StartsWith("a"))
                {
                    argument[1] = argument[1].ToLower();

                    if (argument[1].StartsWith("n"))
                        autoConnect = AutoConnect.NONE;
                    else if (argument[1].StartsWith("o"))
                        autoConnect = AutoConnect.ONE;
                    else if (argument[1].StartsWith("a"))
                        autoConnect = AutoConnect.ANY;
                    else
                        ExitProgram(("Invalid auto connect setting specified <" + argument[1] + ">"), exitCode: -1);
                }

                // set logging mode (overwrite or append)
                else if (argument[0].StartsWith("logm"))
                {
                    argument[1] = argument[1].ToLower();

                    if (argument[1].StartsWith("o"))
                        logMode = FileMode.Create;
                    else if (argument[1].StartsWith("a"))
                        logMode = FileMode.Append;
                    else
                        ExitProgram(("Invalid log mode setting specified <" + argument[1] + ">"), exitCode: -1);
                }

                // specify log file (and enable logging)
                else if (argument[0].StartsWith("lo"))
                {
                    logging = true;
                    logFile = argument[1];
                }

                // an invalid/incomplete argument was passed
                else
                {
                    ExitProgram(("Invalid or incomplete argument <" + arg + ">\nTry 'ss.exe -help' to see a list of valid arguments"), exitCode: -1);
                }
            }

            // if we made it this far, everything has been processed and we're ready to proceed!
        }

        private static void ListAvailablePorts()
        {
            if (availablePorts.Count >= 1)
            {
                Console.WriteLine("\nPORT\tVID\tPID\tDESCRIPTION");
                Console.WriteLine("----------------------------------------------------------------------");
                foreach (ComPort p in availablePorts)
                {
                    Console.WriteLine("{0}\t{1}\t{2}\t{3} {4}",
                        p.name,
                        p.vid,
                        p.pid,
                        (p.isCircuitPython) ? (p.board.make + " " + p.board.model) : p.description,
                        ((p.busDescription.Length > 0) && !p.description.StartsWith(p.busDescription)) ? ("[" + p.busDescription + "]") : ""
                    );
                }
                Console.WriteLine("");
            }
            else
            {
                Console.WriteLine("\nNo COM ports detected.\n");
            }
        }


        /// <summary>
        /// Writes messages using Console.WriteLine() as long as the 'Quiet' option hasn't been enabled
        /// </summary>
        /// <param name="message">Message to output (assuming 'Quiet' is false)</param>
        static void Output(string message, bool force=false, bool newline=true, bool flush=false)
        {
            if (!SimplySerial.Quiet || force)
            {
                if (newline)
                    message += "\n";

                if (message.Length > 0)
                    Console.Write(message);

                if (logging)
                {
                    logData += message;
                    if ((logData.Length >= bufferSize) || flush)
                    {
                        try
                        {
                            FileStream stream = new FileStream(logFile, FileMode.Append, FileAccess.Write);
                            using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
                            {
                                writer.Write(logData);
                            }
                        }
                        catch
                        {
                            Console.WriteLine($"({DateTime.Now}) Error accessing log file '{logFile}'");
                        }
                        logData = string.Empty;
                    }
                }
            }
        }


        /// <summary>
        /// Displays help information about this application and its command-line arguments
        /// </summary>
        static void ShowHelp()
        {
            Console.WriteLine("Usage: ss.exe [-com:PORT] [-baud:RATE] [-parity:PARITY] [-databits:VAL]");
            Console.WriteLine("              [-stopbits:VAL] [-autoconnect:VAL] [-log:LOGFILE] [-logmode:MODE]");
            Console.WriteLine("              [-quiet]\n");
            Console.WriteLine("A basic serial terminal for IoT device programming in general, and working with");
            Console.WriteLine("CircuitPython devices specifically.  With no command-line arguments specified,");
            Console.WriteLine("SimplySerial will attempt to identify and connect to a CircuitPython-capable board");
            Console.WriteLine("at 115200 baud, no parity, 8 data bits and 1 stop bit.  If no known boards are");
            Console.WriteLine("detected, it will default to the first available serial (COM) port at 9600 baud.\n");
            Console.WriteLine("Optional arguments:");
            Console.WriteLine("  -help             Display this help message");
            Console.WriteLine("  -version          Display version and installation information");
            Console.WriteLine("  -list             Display a list of available serial (COM) ports");
            Console.WriteLine("  -com:PORT         COM port number (i.e. 1 for COM1, 22 for COM22, etc.)");
            Console.WriteLine("  -baud:RATE        1200 | 2400 | 4800 | 7200 | 9600 | 14400 | 19200 | 38400 |");
            Console.WriteLine("                    57600 | 115200 | (Any valid baud rate for the specified port.)");
            Console.WriteLine("  -parity:PARITY    NONE | EVEN | ODD | MARK | SPACE");
            Console.WriteLine("  -databits:VAL     4 | 5 | 6 | 7 | 8");
            Console.WriteLine("  -stopbits:VAL     0 | 1 | 1.5 | 2");
            Console.WriteLine("  -autoconnect:VAL  NONE| ONE | ANY, enable/disable auto-(re)connection when");
            Console.WriteLine("                    a device is disconnected / reconnected.");
            Console.WriteLine("  -log:LOGFILE      Logs all output to the specified file.");
            Console.WriteLine("  -logmode:MODE     APPEND | OVERWRITE, default is OVERWRITE");
            Console.WriteLine("  -quiet            don't print any application messages/errors to console");
            Console.WriteLine("  -forcenewline     Force linefeeds (newline) in place of carriage returns in received data.");
            Console.WriteLine("\nPress CTRL-X to exit a running instance of SimplySerial.\n");
        }

        /// <summary>
        /// Displays version and installation information about this application
        /// </summary>
        static void ShowVersion()
        {
            string installType;

            // determine installation type (user/system/standalone)
            if (appFolder.ToLower().Contains("appdata\\roaming"))
                installType = "User";
            else if (appFolder.ToLower().Contains("program files"))
                installType = "System";
            else
                installType = "Standalone/Manual";

            Console.WriteLine($"SimplySerial version {version}");
            Console.WriteLine($"  Installation Type : {installType}");
            Console.WriteLine($"  Installation Path : {appFolder}");
            Console.WriteLine($"  Board Data File   : {boardData.version}\n");
        }


        /// <summary>
        /// Writes the specified exit message to the console, then waits for user to press a key before halting program execution.
        /// </summary>
        /// <param name="message">Message to display - should indicate the reason why the program is terminating.</param>
        /// <param name="exitCode">Code to return to parent process.  Should be &lt;0 if an error occurred, &gt;=0 if program is terminating normally.</param>
        /// <param name="silent">Exits without displaying a message or asking for a key press when set to 'true'</param>
        static void ExitProgram(string message="", int exitCode=0, bool silent=false)
        {
            // the serial port should be closed before exiting
            if (serialPort != null && serialPort.IsOpen)
                serialPort.Close();
            if (!silent)
                Output("\n" + message, flush: true);
            else if (logging)
                Output("", force: true, newline: false, flush: true);
#if DEBUG
            Console.WriteLine("\n>> Press any key to exit <<");
            Console.ReadKey();
#endif
            Environment.Exit(exitCode);
        }


        /// <summary>
        /// Returns a list of available serial ports with their associated PID, VID and descriptions 
        /// Modified from the example written by Kamil Górski (freakone) available at
        /// http://blog.gorski.pm/serial-port-details-in-c-sharp
        /// https://github.com/freakone/serial-reader
        /// Some modifications were based on this stackoverflow thread:
        /// https://stackoverflow.com/questions/11458835/finding-information-about-all-serial-devices-connected-through-usb-in-c-sharp
        /// Hardware Bus Description through WMI is based on Simon Mourier's answer on this stackoverflow thread:
        /// https://stackoverflow.com/questions/69362886/get-devpkey-device-busreporteddevicedesc-from-win32-pnpentity-in-c-sharp
        /// </summary>
        /// <returns>List of available serial ports</returns>
        private static List<ComPort> GetSerialPorts()
        {
            const string vidPattern = @"VID_([0-9A-F]{4})";
            const string pidPattern = @"PID_([0-9A-F]{4})";
            const string namePattern = @"(?<=\()COM[0-9]{1,3}(?=\)$)";
            const string query = "SELECT * FROM Win32_PnPEntity WHERE ClassGuid=\"{4d36e978-e325-11ce-bfc1-08002be10318}\"";

            // as per INTERFACE_PREFIXES in adafruit_board_toolkit
            // (see https://github.com/adafruit/Adafruit_Board_Toolkit/blob/main/adafruit_board_toolkit)
            string[] cpb_descriptions = new string[] { "CircuitPython CDC ", "Sol CDC ", "StringCarM0Ex CDC " };

            List<ComPort> detectedPorts = new List<ComPort>();
            
            foreach (var p in new ManagementObjectSearcher("root\\CIMV2", query).Get().OfType<ManagementObject>())
            {
                ComPort c = new ComPort
                {
                    // extract and clean up port name and number
                    name = p.GetPropertyValue("Name").ToString()
                };

                Match mName = Regex.Match(c.name, namePattern);
                if (mName.Success)
                {
                    c.name = mName.Value;
                    c.num = int.Parse(c.name.Substring(3));
                }
                
                // if the port name or number cannot be determined, skip this port and move on
                if (c.num < 1)
                    continue;

                // get the device's VID and PID
                string pidvid = p.GetPropertyValue("PNPDeviceID").ToString();

                // extract and clean up device's VID
                Match mVID = Regex.Match(pidvid, vidPattern, RegexOptions.IgnoreCase);
                if (mVID.Success)
                    c.vid = mVID.Groups[1].Value.Substring(0, Math.Min(4, c.vid.Length));

                // extract and clean up device's PID
                Match mPID = Regex.Match(pidvid, pidPattern, RegexOptions.IgnoreCase);
                if (mPID.Success)
                    c.pid = mPID.Groups[1].Value.Substring(0, Math.Min(4, c.pid.Length));

                // extract the device's friendly description (caption)
                c.description = p.GetPropertyValue("Caption").ToString();
                
                // attempt to match this device with a known board
                c.board = MatchBoard(c.vid, c.pid);

                // extract the device's hardware bus description
                c.busDescription = "";
                var inParams = new object[] { new string[] { "DEVPKEY_Device_BusReportedDeviceDesc" }, null };
                p.InvokeMethod("GetDeviceProperties", inParams);
                var outParams = (ManagementBaseObject[])inParams[1];
                if (outParams.Length > 0)
                {
                    var data = outParams[0].Properties.OfType<PropertyData>().FirstOrDefault(d => d.Name == "Data");
                    if (data != null)
                    {
                        c.busDescription = data.Value.ToString();
                    }
                }

                // we can determine if this is a CircuitPython board by its bus description
                foreach (string prefix in cpb_descriptions)
                {
                    if (c.busDescription.StartsWith(prefix))
                        c.isCircuitPython = true;
                }

                // add this port to our list of detected ports
                detectedPorts.Add(c); 
            }

            return detectedPorts;
        }


        /// <summary>
        /// Matches to a known development board based on VID and PID
        /// </summary>
        /// <param name="vid">VID of board</param>
        /// <param name="pid">PID of board</param>
        /// <returns>Board structure containing information about the matched board, or generic values otherwise/returns>
        static Board MatchBoard(string vid, string pid)
        {
            Board mBoard = null;
            if (boardData.boards != null)
                mBoard = boardData.boards.Find(b => (b.vid == vid) && (b.pid == pid));

            if (mBoard == null)
            {
                mBoard = new Board(vid:vid, pid:pid);

                Vendor mVendor = null;
                if (boardData.vendors != null)
                    mVendor = boardData.vendors.Find(v => v.vid == vid);
                if (mVendor != null)
                    mBoard.make = mVendor.make;
            }

            return mBoard;
        }
    
        static void LoadBoards()
        {
            try
            {
                using (StreamReader r = new StreamReader($"{appFolder}\\boards.json"))
                {
                    string json = r.ReadToEnd();
                    boardData = JsonConvert.DeserializeObject<BoardData>(json);
                }
            }
            catch (Exception)
            {
                boardData = new BoardData
                {
                    version = "(boards.json is missing or invalid)"
                };
            }
        }
    }

 
    /// <summary>
    /// Custom string array sorting logic for SimplySerial command-line arguments
    /// </summary>
    public class ArgumentSorter : IComparer<string>
    {
        /// <summary>
        /// Checks the first letter/character of incoming strings for a high-priority letter/character and sorts accordingly
        /// </summary>
        /// <param name="x">string to compare</param>
        /// <param name="y">string to compare</param>
        /// <returns>-1 if a priority character is found in string 'x', 1 if a priority character is found in 'y', 0 if neither string has a priority character</returns>
        public int Compare(string x, string y)
        {
            // '?' or 'h' trigger the 'help' text output and supersede all other command-line arguments
            // 'v' triggers the 'version' text output and supersedes all other command-line arguments aside from 'help'
            // 'l' triggers the 'list available ports' output and supersedes all other command-line arguments aside from 'help' and 'version'
            // 'q' enables the 'quiet' option, which needs to be enabled before something that would normally generate console output
            // 'c' is the 'comport' setting, which needs to be processed before 'autoconnect'
            
            x = x.ToLower();
            if (x.StartsWith("lo"))
                x = "z"; // mask out logging options so that they are not interpreted as the list option
            
            y = y.ToLower();
            if (y.StartsWith("lo"))
                y = "z"; // mask out logging options so that they are not interpreted as the list option
            
            foreach (char c in "?hvlqc")
            {
                if (x.ToLower()[0] == c)
                    return (-1);
                else if (y.ToLower()[0] == c)
                    return (1);
            }

            // treat everything else equally, as processing order doesn't matter
            return (0);
        }
    }
}
