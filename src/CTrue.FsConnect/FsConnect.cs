﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.FlightSimulator.SimConnect;
using Serilog;

namespace CTrue.FsConnect
{
    /// <inheritdoc />
    public class FsConnect : IFsConnect
    {
        private SimConnect _simConnect = null;

        private readonly EventWaitHandle _simConnectEventHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        private readonly List<InputEventInfo> _inputEventInfoList = new List<InputEventInfo>();

        private Thread _simConnectReceiveThread = null;
        private readonly FsConnectionInfo _connectionInfo = new FsConnectionInfo();
        private bool _paused = false;
        private int _nextId = (int)FsConnectEnum.Base;
        
        #region Simconnect structures

        private enum SimEvents : uint
        {
            EVENT_AIRCRAFT_LOAD,
            EVENT_FLIGHT_LOAD,
            EVENT_PAUSED,
            EVENT_PAUSE,
            EVENT_SIM,
            EVENT_CRASHED,
            ObjectAdded,
            ObjectRemoved,
            PauseSet,
            SetText
        }

        enum GROUP_IDS : uint
        {
            GROUP_1 = 98
        }

        #endregion

        /// <inheritdoc />
        public event EventHandler<bool> ConnectionChanged;

        /// <inheritdoc />
        public event EventHandler<FsDataReceivedEventArgs> FsDataReceived;

        /// <inheritdoc />
        public event EventHandler<ObjectAddRemoveEventReceivedEventArgs> ObjectAddRemoveEventReceived;

        /// <inheritdoc />
        public event EventHandler<FsErrorEventArgs> FsError;

        /// <inheritdoc />
        public event EventHandler AircraftLoaded;

        /// <inheritdoc />
        public event EventHandler FlightLoaded;

        /// <inheritdoc />
        public event EventHandler<PauseStateChangedEventArgs> PauseStateChanged;

        /// <inheritdoc />
        public event EventHandler<SimStateChangedEventArgs> SimStateChanged;

        /// <inheritdoc />
        public event EventHandler Crashed;

        /// <inheritdoc />
        public bool Connected
        {
            get => _connectionInfo.Connected;
            private set
            {
                if (_connectionInfo.Connected != value)
                {
                    _connectionInfo.Connected = value;
                    
                    ConnectionChanged?.Invoke(this, value);
                }
            }
        }

        /// <inheritdoc />
        public FsConnectionInfo ConnectionInfo => _connectionInfo;

        /// <inheritdoc />
        public SimConnectFileLocation SimConnectFileLocation { get; set; } = SimConnectFileLocation.Local;

        /// <inheritdoc />
        public bool Paused => _paused;

        /// <inheritdoc />
        public void Connect(string applicationName, uint configIndex = 0)
        {
            try
            {
                _simConnect = new SimConnect(applicationName, IntPtr.Zero, 0, _simConnectEventHandle, configIndex);
            }
            catch (Exception e)
            {
                _simConnect = null;
                throw new Exception("Could not connect to Flight Simulator: " + e.Message, e);
            }

            _simConnectReceiveThread = new Thread(new ThreadStart(SimConnect_MessageReceiveThreadHandler));
            _simConnectReceiveThread.IsBackground = true;
            _simConnectReceiveThread.Start();

            _simConnect.OnRecvOpen += new SimConnect.RecvOpenEventHandler(SimConnect_OnRecvOpen);
            _simConnect.OnRecvQuit += new SimConnect.RecvQuitEventHandler(SimConnect_OnRecvQuit);

            _simConnect.OnRecvException += new SimConnect.RecvExceptionEventHandler(SimConnect_OnRecvException);
            _simConnect.OnRecvSimobjectData += new SimConnect.RecvSimobjectDataEventHandler(SimConnect_RecvSimObjectData);
            _simConnect.OnRecvSimobjectDataBytype += new SimConnect.RecvSimobjectDataBytypeEventHandler(SimConnect_OnRecvSimobjectDataBytype);
            _simConnect.OnRecvEventObjectAddremove += new SimConnect.RecvEventObjectAddremoveEventHandler(SimConnect_OnRecvEventObjectAddremoveEventHandler);

            _simConnect.OnRecvEvent += SimConnect_OnRecvEvent;

            // System events
            _simConnect.SubscribeToSystemEvent(SimEvents.EVENT_AIRCRAFT_LOAD, "AircraftLoaded");
            _simConnect.SubscribeToSystemEvent(SimEvents.EVENT_FLIGHT_LOAD, "FlightLoaded");
            _simConnect.SubscribeToSystemEvent(SimEvents.EVENT_PAUSED, "Paused");
            _simConnect.SubscribeToSystemEvent(SimEvents.EVENT_PAUSE, "Pause");
            _simConnect.SubscribeToSystemEvent(SimEvents.EVENT_SIM, "Sim");
            _simConnect.SubscribeToSystemEvent(SimEvents.EVENT_CRASHED, "Crashed");
            _simConnect.SubscribeToSystemEvent(SimEvents.ObjectAdded, "ObjectAdded");
            _simConnect.SubscribeToSystemEvent(SimEvents.ObjectRemoved, "ObjectRemoved");

            // Client events
            _simConnect.MapClientEventToSimEvent(SimEvents.PauseSet, "PAUSE_SET");
        }

        /// <inheritdoc />
        public bool RegisterInputEvent(InputEventInfo inputEventInfo)
        {
            if (GetInputEventHandler((uint)(object)inputEventInfo.NotificationGroupId,
                    (uint)(object)inputEventInfo.ClientEventId, out _))
            {
                Log.Warning("Input event already registered  g:{inputGroup} e:{inputEventId} '{inputDefinition}'", inputEventInfo.NotificationGroupId, inputEventInfo.ClientEventId, inputEventInfo.InputDefinition);
                return false;
            }

            // Setup client event
            _simConnect.MapClientEventToSimEvent(inputEventInfo.ClientEventId, inputEventInfo.ClientEventName);
            _simConnect.AddClientEventToNotificationGroup(inputEventInfo.NotificationGroupId, inputEventInfo.ClientEventId, false);
            _simConnect.SetNotificationGroupPriority(inputEventInfo.NotificationGroupId, 1);

            // Setup input event mapping
            _simConnect.MapInputEventToClientEvent(
                inputEventInfo.InputGroup,
                inputEventInfo.InputDefinition,
                inputEventInfo.ClientEventId,
                1,
                (FsConnectEnum)SimConnect.SIMCONNECT_UNUSED,
                0,
                false
            );

            _simConnect.SetInputGroupPriority(inputEventInfo.InputGroup, 1);

            _inputEventInfoList.Add(inputEventInfo);

            Log.Information("Input event g:{inputGroup} e:{inputEventId} registration complete for '{inputDefinition}'", inputEventInfo.NotificationGroupId, inputEventInfo.ClientEventId, inputEventInfo.InputDefinition);

            return true;
        }

        /// <inheritdoc />
        public void Connect(string applicationName, string hostName, uint port, SimConnectProtocol protocol)
        {
            if (applicationName == null) throw new ArgumentNullException(nameof(applicationName));

            CreateSimConnectConfigFile(hostName, port, protocol);

            Connect(applicationName);
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            if (!Connected) return;

            try
            {
                Log.Debug("Disconnecting from Flight Simulator. Unsubscribing from events.");
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.EVENT_AIRCRAFT_LOAD);
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.EVENT_FLIGHT_LOAD);
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.EVENT_PAUSED);
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.EVENT_PAUSE);
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.EVENT_SIM);
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.EVENT_CRASHED);
                _simConnect.UnsubscribeFromSystemEvent(SimEvents.ObjectAdded);

                _simConnect.RemoveClientEvent(GROUP_IDS.GROUP_1, SimEvents.PauseSet);
                
                _inputEventInfoList.ForEach(iei =>
                {
                    _simConnect.RemoveClientEvent(iei.NotificationGroupId, iei.ClientEventId);
                    _simConnect.RemoveInputEvent(iei.InputGroup, iei.InputDefinition);
                });

                _simConnectReceiveThread.Abort();
                _simConnectReceiveThread.Join();

                _simConnect.OnRecvOpen -= new SimConnect.RecvOpenEventHandler(SimConnect_OnRecvOpen);
                _simConnect.OnRecvQuit -= new SimConnect.RecvQuitEventHandler(SimConnect_OnRecvQuit);
                _simConnect.OnRecvException -= new SimConnect.RecvExceptionEventHandler(SimConnect_OnRecvException);
                _simConnect.OnRecvSimobjectData -= new SimConnect.RecvSimobjectDataEventHandler(SimConnect_RecvSimObjectData);
                _simConnect.OnRecvSimobjectDataBytype -= new SimConnect.RecvSimobjectDataBytypeEventHandler(SimConnect_OnRecvSimobjectDataBytype);
                _simConnect.OnRecvEvent -= SimConnect_OnRecvEvent;

                _simConnect.Dispose();
            }
            catch
            {
                // ignored
            }
            finally
            {
                _simConnectReceiveThread = null;
                _simConnect = null;

                _connectionInfo.ApplicationName = "";
                _connectionInfo.ApplicationVersion = "";
                _connectionInfo.ApplicationBuild = "";
                _connectionInfo.SimConnectBuild = "";

                Connected = false;
            }

            Log.Information("Disconnected from Flight Simulator");
        }

        #region RegisterDataDefinition

        /// <inheritdoc />
        public int RegisterDataDefinition<T>(Enum id, List<SimVar> definition) where T : struct
        {
            foreach (var item in definition)
            {
                _simConnect.AddToDataDefinition(id, item.Name, item.Unit, item.DataType, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            }

            _simConnect.RegisterDataDefineStruct<T>(id);

            return Convert.ToInt32(id);
        }

        /// <inheritdoc />
        public int RegisterDataDefinition<T>(int id, List<SimVar> definition) where T : struct
        {
            return RegisterDataDefinition<T>((FsConnectEnum) id, definition);
        }

        /// <inheritdoc />
        public int RegisterDataDefinition<T>(List<SimVar> definition) where T : struct
        {
            int nextId = GetNextId();

            return RegisterDataDefinition<T>(nextId, definition);
        }

        /// <inheritdoc />
        public int RegisterDataDefinition<T>(Enum defineId) where T : struct
        {
            SimVarReflector reflector = new SimVarReflector();
            List<SimVar> definition = reflector.GetSimVars<T>();

            RegisterDataDefinition<T>(defineId, definition);

            return Convert.ToInt32(defineId);
        }

        /// <inheritdoc />
        public int RegisterDataDefinition<T>(int defineId) where T : struct
        {
            return RegisterDataDefinition<T>((FsConnectEnum)defineId);
        }

        /// <inheritdoc />
        public int RegisterDataDefinition<T>() where T : struct
        {
            return RegisterDataDefinition<T>(GetNextId());
        }

        #endregion

        #region RequestData

        /// <inheritdoc />
        public void RequestDataOnSimObject(Enum requestId, Enum defineId, uint objectId, FsConnectPeriod period, FsConnectDRequestFlag flags, uint origin, uint interval, uint limit) 
        {
            _simConnect?.RequestDataOnSimObject(requestId, defineId, objectId, (SIMCONNECT_PERIOD)period, (SIMCONNECT_DATA_REQUEST_FLAG)flags, origin, interval, limit);
        }

        /// <inheritdoc />
        public void RequestDataOnSimObject(Enum requestId, int defineId, uint objectId, FsConnectPeriod period, FsConnectDRequestFlag flags, uint origin, uint interval, uint limit)
        {
            _simConnect?.RequestDataOnSimObject(requestId, (FsConnectEnum)defineId, objectId, (SIMCONNECT_PERIOD)period, (SIMCONNECT_DATA_REQUEST_FLAG)flags, origin, interval, limit);
        }

        /// <inheritdoc />
        public void RequestData(Enum requestId, Enum defineId, uint radius = 0, FsConnectSimobjectType type = FsConnectSimobjectType.User)
        {
            _simConnect?.RequestDataOnSimObjectType(requestId, defineId, radius, (SIMCONNECT_SIMOBJECT_TYPE)type);
        }

        /// <inheritdoc />
        public void RequestData(int requestId, int defineId, uint radius = 0, FsConnectSimobjectType type = FsConnectSimobjectType.User)
        {
            _simConnect?.RequestDataOnSimObjectType((FsConnectEnum)requestId, (FsConnectEnum)defineId, radius, (SIMCONNECT_SIMOBJECT_TYPE)type);
        }

        #endregion

        /// <inheritdoc />
        public void UpdateData<T>(Enum defineId, T data, uint objectId = 1)
        {
            _simConnect?.SetDataOnSimObject(defineId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
        }

        /// <inheritdoc />
        public void UpdateData<T>(int defineId, T data, uint objectId = 1)
        {
            _simConnect?.SetDataOnSimObject((FsConnectEnum)defineId, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
        }

        /// <summary>
        /// Gets the next id, for definitions and other SimConnect artifacts that require it.
        /// </summary>
        /// <returns>Returns an int that can be used to identifying SimConnect artifacts, such as definitions and events.</returns>
        public int GetNextId()
        {
            return _nextId++;
        }

        /// <inheritdoc />
        public void MapClientEventToSimEvent(Enum groupId, Enum eventId, string eventName)
        {
            _simConnect.MapClientEventToSimEvent(eventId, eventName);
            _simConnect.AddClientEventToNotificationGroup(groupId, eventId, false);
        }

        /// <inheritdoc />
        public void MapClientEventToSimEvent(Enum groupId, Enum eventId, FsEventNameId eventNameId)
        {
            string eventName = FsEventNameLookup.GetFsEventName(eventNameId);
            _simConnect.MapClientEventToSimEvent(eventId, eventName);
            _simConnect.AddClientEventToNotificationGroup(groupId, eventId, false);
        }

        /// <inheritdoc />
        public void MapClientEventToSimEvent(int groupId, int eventId, string eventName)
        {
            MapClientEventToSimEvent((FsConnectEnum)groupId, (FsConnectEnum)eventId, eventName);
        }

        /// <inheritdoc />
        public void MapClientEventToSimEvent(int groupId, int eventId, FsEventNameId eventNameId)
        {
            MapClientEventToSimEvent((FsConnectEnum)groupId, (FsConnectEnum)eventId, eventNameId);
        }

        /// <inheritdoc />
        public void SetNotificationGroupPriority(Enum groupId)
        {
            _simConnect.SetNotificationGroupPriority(groupId, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
        }

        /// <inheritdoc />
        public void SetNotificationGroupPriority(int groupId)
        {
            SetNotificationGroupPriority((FsConnectEnum) groupId);
        }

        /// <inheritdoc />
        public void TransmitClientEvent(Enum eventId, uint dwData, Enum groupId)
        {
            _simConnect.TransmitClientEvent((uint)SIMCONNECT_SIMOBJECT_TYPE.USER, eventId, dwData, groupId, SIMCONNECT_EVENT_FLAG.DEFAULT);
        }

        /// <inheritdoc />
        public void TransmitClientEvent(int eventId, uint dwData, int groupId)
        {
            _simConnect.TransmitClientEvent((uint)SIMCONNECT_SIMOBJECT_TYPE.USER, (FsConnectEnum)eventId, dwData, (FsConnectEnum)groupId, SIMCONNECT_EVENT_FLAG.DEFAULT);
        }

        /// <inheritdoc />
        public void SetText(string text, int duration)
        {
            _simConnect.Text(SIMCONNECT_TEXT_TYPE.PRINT_BLACK, duration, SimEvents.SetText, text);
        }

        /// <inheritdoc />
        public void Pause()
        {
            Pause(!_paused);
        }

        /// <inheritdoc />
        public void Pause(bool pause)
        {
            _simConnect.TransmitClientEvent(0, SimEvents.PauseSet, pause ? (uint)1 : (uint)0, GROUP_IDS.GROUP_1, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            _paused = true;
        }

        #region Event Handlers

        private void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            Log.Debug("OnRecvOpen (Size: {Size}, Version: {Version}, Id: {Id})", data.dwSize, data.dwVersion, data.dwID);

            _connectionInfo.ApplicationName = data.szApplicationName;
            _connectionInfo.ApplicationVersion = $"{data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}";
            _connectionInfo.ApplicationBuild = $"{data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor}";
            _connectionInfo.SimConnectVersion = $"{data.dwSimConnectVersionMajor}.{data.dwSimConnectVersionMinor}";
            _connectionInfo.SimConnectBuild = $"{data.dwSimConnectBuildMajor}.{data.dwSimConnectBuildMinor}";

            Connected = true;
        }

        private void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            Log.Debug("OnRecvQuit (S: {Size}, V: {Version}, I: {Id})", data.dwSize, data.dwVersion, data.dwID);
            Disconnect();
        }

        private void SimConnect_OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            Log.Debug("OnRecvEvent ({Size}b/{Version}/{Id}) g:{GroupId}, e:{EventId}, d:{Data}", data.dwSize, data.dwVersion, data.dwID, data.uGroupID, data.uEventID, data.dwData);

            if (data.uEventID == (uint)SimEvents.EVENT_AIRCRAFT_LOAD)
            {
                Log.Debug("SysEvent: Aircraft loaded");
                AircraftLoaded?.Invoke(this, EventArgs.Empty);
            }
            else if (data.uEventID == (uint)SimEvents.EVENT_FLIGHT_LOAD)
            {
                Log.Debug("SysEvent: Flight loaded");
                FlightLoaded?.Invoke(this, EventArgs.Empty);
            }
            else if (data.uEventID == (uint)SimEvents.EVENT_SIM)
            {
                Log.Debug("SysEvent: Running: {Running}", data.dwData == 1);
                SimStateChanged?.Invoke(this, new SimStateChangedEventArgs() { Running = data.dwData == 1 });
            }
            else if (data.uEventID == (uint)SimEvents.EVENT_CRASHED)
            {
                Log.Debug("SysEvent: Crashed");
                Crashed?.Invoke(this, EventArgs.Empty);
            }
            else if (data.uEventID == (uint)SimEvents.EVENT_PAUSE)
            {
                _paused = data.dwData == 1;
                Log.Debug("ClientEvent: Paused: {Paused}", _paused);
                PauseStateChanged?.Invoke(this, new PauseStateChangedEventArgs() { Paused = data.dwData == 1 });
            }
            else if (GetInputEventHandler(data.uGroupID, data.uEventID, out var iei))
            {
                Log.Information("Handling input event: {uGroupID} / {uEventID}", data.uGroupID, data.uEventID);
                iei.RaiseInputEvent();
            }
        }

        private bool GetInputEventHandler(uint groupId, uint eventId, out InputEventInfo iei)
        {
            iei = _inputEventInfoList.FirstOrDefault(i =>
                (uint)(object)i.NotificationGroupId == groupId && (uint)(object)i.ClientEventId == eventId);

            return iei != null;
        }

        private void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            Log.Warning("OnRecvException ({Size}b/{Version}/{Id}) Exception: {Exception}, SendId: {SendId}, Index: {Index}", data.dwSize, data.dwVersion, data.dwID, ((FsException)data.dwException).ToString(), data.dwSendID, data.dwIndex);
           
            FsError?.Invoke(this, new FsErrorEventArgs()
            {
                ExceptionCode = (FsException)data.dwException,
                ExceptionDescription = ((FsException)data.dwException).ToString(),
                SendID = data.dwSendID,
                Index = data.dwIndex
            });
        }

        private void SimConnect_RecvSimObjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            Log.Debug("RecvSimObjectData (S: {Size}, V: {Version}, I: {Id}) RequestID: {RequestID}, ObjectID: {ObjectID}, DefineID: {DefineID}, Flags: {Flags},EntryNumber: {EntryNumber}, OutOf: {OutOf}, DefineCount: {DefineCount}, DataItems: {DataItems}", data.dwSize, data.dwVersion, data.dwID, data.dwRequestID, data.dwObjectID, data.dwDefineID, data.dwFlags, data.dwentrynumber, data.dwoutof, data.dwDefineCount, data.dwData.Length);

            FsDataReceived?.Invoke(this, new FsDataReceivedEventArgs()
            {
                RequestId = data.dwRequestID,
                ObjectID = data.dwObjectID,
                DefineId = data.dwDefineID,
                Flags = data.dwFlags,
                Data = data.dwData.ToList(),
                EntryNumber = data.dwentrynumber,
                OutOf = data.dwoutof,
                DefineCount = data.dwDefineCount,
                DataItemCount = data.dwData.Length
            });
        }

        private void SimConnect_OnRecvSimobjectDataBytype(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
        {
            Log.Debug("RecvSimObjectData (S: {Size}, V: {Version}, I: {Id}) RequestID: {RequestID}, ObjectID: {ObjectID}, DefineID: {DefineID}, Flags: {Flags},EntryNumber: {EntryNumber}, OutOf: {OutOf}, DefineCount: {DefineCount}, DataItems: {DataItems}", data.dwSize, data.dwVersion, data.dwID, data.dwRequestID, data.dwObjectID, data.dwDefineID, data.dwFlags, data.dwentrynumber, data.dwoutof, data.dwDefineCount, data.dwData.Length);

            FsDataReceived?.Invoke(this, new FsDataReceivedEventArgs()
            {
                RequestId = data.dwRequestID,
                ObjectID = data.dwObjectID,
                DefineId = data.dwDefineID,
                Flags = data.dwFlags,
                Data = data.dwData.ToList(),
                EntryNumber = data.dwentrynumber,
                OutOf = data.dwoutof,
                DefineCount = data.dwDefineCount,
                DataItemCount = data.dwData.Length
            });
        }

        private void SimConnect_OnRecvEventObjectAddremoveEventHandler(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            SIMCONNECT_RECV_EVENT_OBJECT_ADDREMOVE eventData = (SIMCONNECT_RECV_EVENT_OBJECT_ADDREMOVE)data;
            Log.Debug("OnRecvEventObjectAddremoveEventHandler ({Size}b/{Version}/{Id}) EventId: {EventId}, Added: {Added}, ObjType: {ObjType}, ObjId: {ObjId}", data.dwSize, data.dwVersion, data.dwID, data.uEventID, data.uEventID == (uint)SimEvents.ObjectAdded, eventData.eObjType, data.dwData);

            ObjectAddRemoveEventReceived?.Invoke(this, new ObjectAddRemoveEventReceivedEventArgs()
            {
                Added = data.uEventID == (uint)SimEvents.ObjectAdded,
                Data = data.dwData,
                ObjectType = eventData.eObjType,
                ObjectID = data.dwData
            });
        }

        #endregion

        private void SimConnect_MessageReceiveThreadHandler()
        {
            while (true)
            {
                _simConnectEventHandle.WaitOne();

                try
                {
                    _simConnect?.ReceiveMessage();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void CreateSimConnectConfigFile(string hostName, uint port, SimConnectProtocol protocol)
        {
            try
            {
                StringBuilder sb = new StringBuilder();

                string protocolString = "Ipv4";

                switch (protocol)
                {
                    case SimConnectProtocol.Pipe:
                        protocolString = "Pipe";
                        break;
                    case SimConnectProtocol.Ipv4:
                        protocolString = "Ipv4";
                        break;
                    case SimConnectProtocol.Ipv6:
                        protocolString = "Ipv6";
                        break;
                }

                sb.AppendLine("[SimConnect]");
                sb.AppendLine("Protocol=" + protocolString);
                sb.AppendLine($"Address={hostName}");
                sb.AppendLine($"Port={port}");

                string directory = "";
                if (SimConnectFileLocation == SimConnectFileLocation.Local)
                    directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                else
                    directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                string fileName = Path.Combine(directory, "SimConnect.cfg");

                File.WriteAllText(fileName, sb.ToString());
            }
            catch (Exception e)
            {
                throw new Exception("Could not create SimConnect.cfg file: " + e.Message, e);
            }
        }

        // To detect redundant calls
        private bool _disposed = false;

        /// <summary>
        /// Disconnects and disposes the client.
        /// </summary>
        public void Dispose() => Dispose(true);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed state (managed objects).
                _simConnect?.Dispose();
            }

            _disposed = true;
        }

        public void RequestSystemState(Enum id, String state)
        {
            _simConnect?.RequestSystemState(id, state);
        }

        public void SendEvent(Enum id, int parameter = 0)
        {
            _simConnect?.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, id, (uint)parameter, GROUP_IDS.GROUP_1, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }

        public void RequestPeriodicData(Enum requestId)
        {
            _simConnect?.RequestDataOnSimObject(requestId, requestId, 0, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }

        public void UnregisterPeriodicDataRequest(Enum requestId)
        {
            _simConnect?.RequestDataOnSimObject(requestId, requestId, 0, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }

    }
}
