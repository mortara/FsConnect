﻿using CommandLine;
using Serilog.Events;

namespace CTrue.FsConnect.TestConsole
{
    public class Options
    {
        [Option('h', "hostname", SetName = "connect", HelpText = "Sets the hostname of the host that is running Flight Simulator.")]
        public string Hostname { get; set; }

        [Option('p', "port", SetName = "connect", HelpText = "Sets the TCP port that Flight Simulator is being hosting on.")]
        public uint Port { get; set; }

        [Option('i', "index", SetName = "config", HelpText = "Specifies the config index in SimConnect.cfg to use.")]
        public uint ConfigIndex { get; set; }

        [Option('l', "loglevel", HelpText = "Specifies the log level.", Default = LogEventLevel.Warning)]
        public LogEventLevel LogLevel { get; set; }

        [Option('j', "joystick",
            HelpText =
                "Provide the joystick id, the index in list of joysticks, to handle joystick events. (Use Setup USB Game Controllers app)",
            Required = false, Default = 0)]
        public uint JoystickId { get; set; }
    }
}