using System.Collections.Generic;
using System.Linq;

namespace SimplySerial
{
    public enum AutoConnect { NONE, ONE, ANY };
    public enum AutoDetectType { CircuitPython, USB, ALL };

    public class BoardData
    {
        public string version = "";
        public List<Vendor> vendors;
        public List<Board> boards;
    }
    
    public class Board
    {
        public string vid;
        public string pid;
        public string make;
        public string model;
    
        public Board(string vid="----", string pid="----", string make="", string model="")
        {
            this.vid = vid.ToUpper();
            this.pid = pid.ToUpper();
            
            if (make != "")
                this.make = make;
            else
                this.make = $"VID:{this.vid}";

            if (model != "")
                this.model = model;
            else
                this.model = $"PID:{this.pid}";
        }
    }

    public class Vendor
    {
        public string vid = "----";
        public string make = "VID";
    }

    /// <summary>
    /// Custom structure containing the name, VID, PID and description of a serial (COM) port
    /// Modified from the example written by Kamil Górski (freakone) available at
    /// http://blog.gorski.pm/serial-port-details-in-c-sharp
    /// https://github.com/freakone/serial-reader
    /// </summary>
    public class ComPort // custom struct with our desired values
    {
        public string name;
        public int num = -1;
        public string vid = "----";
        public string pid = "----";
        public string description;
        public string busDescription;
        public Board board;
        public bool isCircuitPython = false;
        //public bool isUSB = false;

        public bool isUSB
        {
            get 
            {
                if (vid == "----" && pid == "----")
                    return false;
                else
                    return true;
            }
        }
    }

    public class ComPortList // Custom struct for collection of ComPorts with some state.
    {
        private static ComPort comPort = new ComPort();
        private static bool validatedPort = false;
        public static bool nextPort = false;

        public static bool isPortAvailable
        {
            get 
            {
                if (availablePorts.Count() >= 1)
                    return true;
                else
                    return false;
            }

        }

        private static bool listUpdated = false;
        public static bool isListUpdated
        {
            get
            {
                return listUpdated;
            }
        }

        private static List<ComPort> availablePorts = new List<ComPort>();
        public static List<ComPort> AvailablePorts
        {
            get 
            {
                return availablePorts;
            }
            set 
            {
                listUpdated = false;
                if (availablePorts.Count != value.Count)
                    listUpdated = true;
                availablePorts = value;
            }
        }

        public static bool ValidPort(string name)
        {
            if (availablePorts.Exists(p => p.name == name))
            {
                comPort = availablePorts.Find(p => p.name == name);
                validatedPort = true;
            }
            else
            {
                comPort = new ComPort();
                validatedPort = false;
            }

            return validatedPort;
        }

        public static bool ValidPort(int num)
        {
            if (availablePorts.Exists(p => p.num == num))
            {
                validatedPort = true;
            }
            else
            {
                validatedPort = false;
            }

            return validatedPort;
        }

        private static string GetNames()
        {
            return string.Join(",", availablePorts.Select(o => o.name));
        }

        private static IEnumerable<int> GetAllNum()
        {
            return availablePorts.Select(o => o.num);
        }

        private static List<ComPort> GetAllUsb()
        {
            return availablePorts.FindAll(p => p.isUSB == true);
        }

        private static ComPort GetFirstCircuitPython()
        {
            return availablePorts.Find(p => p.isCircuitPython == true);
        }

        private static ComPort GetFirstUsb()
        {
            return availablePorts.Find(p => p.isUSB == true);
        }

        public static ComPort GetPort(AutoDetectType autoDetectType = AutoDetectType.USB)
        {
            // If we have a valid port already, and we dont want another one, just return it
            if (!nextPort && ValidPort(comPort.name))
                return comPort;

            // Compare Last and Current Port and CP / USB flags
            if (nextPort)
            {
                // TODO: Fix next comport selection
                // first port isnt coming from the list?
                nextPort = false;
                if (availablePorts.Count > 1)
                {
                    int lastComPort = availablePorts.FindIndex(p => p.num == comPort.num);
                    if (lastComPort >= 0 && lastComPort < availablePorts.Count - 1)
                        comPort = availablePorts[lastComPort + 1];
                    else
                        comPort = availablePorts[0];
                }

                return comPort;
            }
            else
            {
                switch (autoDetectType)
                {
                    case AutoDetectType.CircuitPython:
                        return GetFirstCircuitPython();
                    case AutoDetectType.USB:
                        return GetFirstUsb();
                    default:
                        return availablePorts[0];
                }
            }
        }

        public static ComPort GetPort(int ComNum)
        {
            return availablePorts.Find(p => p.num == ComNum);
        }

        public static ComPort GetPort(string Name)
        {
            return availablePorts.Find(p => p.name == Name);
        }
    }

}
