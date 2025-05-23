using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Text;  //For File Encoding
using Newtonsoft.Json.Linq; // Needed for JObject
using System.IO;    // Need for read/write JSON settings file
using SimHub;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimHub.Plugins.DataPlugins.DataCore;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock;
using MahApps.Metro.Behaviours;


namespace Redadeg.lmuDataPlugin
{
    [PluginName("Redadeg LMU Data plugin")]
    [PluginDescription("Plugin for Redadeg Dashboards \nWorks for LMU")]
    [PluginAuthor("Bobokhidze T.B.")]

    //the class name is used as the property headline name in SimHub "Available Properties"
    public class lmuDataPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private const string PLUGIN_CONFIG_FILENAME = "Redadeg.lmuDataPlugin.json";

        private Thread lmu_extendedThread;
        private Thread lmuCalculateConsumptionsThread;
        private Thread lmuGetJSonDataThread;

        private SettingsControl settingsControlwpf;

        private CancellationTokenSource cts = new CancellationTokenSource();
        private CancellationTokenSource ctsGetJSonDataThread = new CancellationTokenSource();
        private CancellationTokenSource ctsCalculateConsumptionsThread = new CancellationTokenSource();

        public bool IsEnded { get; private set; }
        public bool GetJSonDataIsEnded { get; private set; }
        public bool CalculateConsumptionsIsEnded { get; private set; }

        public PluginManager PluginManager { get; set; }

        public bool StopUpdate;
        public int Priority => 1;

        //input variables
        private string curGame;
        private bool GameInMenu = true;
        private bool GameRunning = false;
        private bool GamePaused = true;
        private bool GameReplay = true;
        private Dictionary<string, string> frontABR;
        private Dictionary<string, string> rearABR;
        int[] lapsForCalculate = new int[] { };
        private Guid SessionId;

        //output variables
        private float[] TyreDiameter = new float[] { 0f, 0f, 0f, 0f };   // in meter - FL,FR,RL,RR
        private float[] LngWheelSlip = new float[] { 0f, 0f, 0f, 0f }; // Longitudinal Wheel Slip values FL,FR,RL,RR
        
        private List<float> LapTimes = new List<float>();
        private List<float> EnergyConsuptions = new List<float>();
        private List<float> ClearEnergyConsuptions = new List<float>();
        private List<float> FuelConsuptions = new List<float>();
        private List<float> FuelRatioAvg = new List<float>();

        private int energy_CurrentIndex = 0;
        private int fuelratio_CurrentIndex = 0;

        private TimeSpan outFromPitTime = TimeSpan.FromSeconds(0);
        private bool OutFromPitFlag = false;
        private TimeSpan InToPitTime = TimeSpan.FromSeconds(0);
        private bool InToPitFlag = false;
        private bool IsLapValid = true;
        private bool LapInvalidated = false;
        private int pitStopUpdatePause = -1;
        private double sesstionTimeStamp = 0; 
        private float lastLapTime = 0;
        private const int updateDataDelayTimer = 10;
        private int updateConsuptionDelayCounter = 0;
        private bool updateConsuptionFlag = false;
        private bool NeedUpdateData = false;
        private const double PsiConvert = 0.14503773773020923;
        private const double zeroKelvin = 273.15;

        private bool loadSessionStaticInfoFromWS = true; // set to true, to force loading data if simhub is launch after the session

        JObject pitMenuH;

        MappedBuffer<LMU_Extended> extendedBuffer = new MappedBuffer<LMU_Extended>(LMU_Constants.MM_EXTENDED_FILE_NAME, false /*partial*/, true /*skipUnchanged*/);
        MappedBuffer<rF2Scoring> scoringBuffer = new MappedBuffer<rF2Scoring>(LMU_Constants.MM_SCORING_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
        MappedBuffer<rF2Rules> rulesBuffer = new MappedBuffer<rF2Rules>(LMU_Constants.MM_RULES_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);

        LMU_Extended lmu_extended;
        rF2Rules rules;       
        bool lmu_extended_connected = false;
        bool rf2_score_connected = false;
        private HttpClient _httpClient;


        // <summary>
        // The Init Method is called by SimHub when plugin is loaded
        // </summary>
        // <param name="pluginManager"></param>
        public void Init(PluginManager pluginManager)
        {
            _httpClient = new HttpClient();
            LapTimes = new List<float>();
            EnergyConsuptions = new List<float>();
            ClearEnergyConsuptions = new List<float>();
            FuelConsuptions = new List<float>();

            //***** Read persistant data config for plugin
            
            // Read the data from file 
            // set path/filename for settings file
            LMURepairAndRefuelData.path = PluginManager.GetCommonStoragePath(PLUGIN_CONFIG_FILENAME);
            try
            {
                // try to read settings file
                JObject JSONSettingsdata = JObject.Parse(File.ReadAllText(LMURepairAndRefuelData.path));
                ButtonBindSettings.Clock_Format24 = JSONSettingsdata["Clock_Format24"] != null ? (bool)JSONSettingsdata["Clock_Format24"] : false;
                ButtonBindSettings.RealTimeClock = JSONSettingsdata["RealTimeClock"] != null ? (bool)JSONSettingsdata["RealTimeClock"] : false;
                ButtonBindSettings.GetMemoryDataThreadTimeout = JSONSettingsdata["GetMemoryDataThreadTimeout"] != null ? (int)JSONSettingsdata["GetMemoryDataThreadTimeout"] : 50;
                ButtonBindSettings.DataUpdateThreadTimeout = JSONSettingsdata["DataUpdateThreadTimeout"] != null ? (int)JSONSettingsdata["DataUpdateThreadTimeout"] : 100;
            }
            catch { }

            //***** Start the internal Thread which communicate with LMU
            // Memory shared communication
            lmu_extendedThread = new Thread(lmu_extendedReadThread);
            lmu_extendedThread.Name = "ExtendedDataUpdateThread";
            lmu_extendedThread.Start();
            
            // API WEB call
            lmuGetJSonDataThread = new Thread(lmu_GetJSonDataThread);
            lmuGetJSonDataThread.Name = "GetJSonDataThread";
            lmuGetJSonDataThread.Start();

            // Computing thread over data
            lmuCalculateConsumptionsThread = new Thread(lmu_CalculateConsumptionsThread);
            lmuCalculateConsumptionsThread.Name = "CalculateConsumptionsThread";
            lmuCalculateConsumptionsThread.Start();

            //***** Init Properties and Data SimHUB
            addPropertyToSimHUB(pluginManager);
            initFrontABRDict();
            initBackABRDict();
        }

        // <summary>
        // Called at plugin manager stop, close/displose anything needed here !
        // </summary>
        // <param name="pluginManager"></param>
        public void End(PluginManager pluginManager)
        {
            IsEnded = true;
            CalculateConsumptionsIsEnded = true;
            GetJSonDataIsEnded = true;
            cts.Cancel();
            ctsGetJSonDataThread.Cancel();
            ctsCalculateConsumptionsThread.Cancel();
            lmu_extendedThread.Join();
            lmuGetJSonDataThread.Join();
            lmuCalculateConsumptionsThread.Join();
            // try to read complete data file from disk, compare file data with new data and write new file if there are diffs
            try
            {
                if (rf2_score_connected) this.scoringBuffer.Disconnect();
                if(lmu_extended_connected) this.extendedBuffer.Disconnect();
                if (lmu_extended_connected) this.rulesBuffer.Disconnect();
            }
            // if there is not already a settings file on disk, create new one and write data for current game
            catch (FileNotFoundException)
            {
                // try to write data file
               
            }
            // other errors like Syntax error on JSON parsing, data file will not be saved
            catch (Exception ex)
            {
                Logging.Current.Info("Plugin Redadeg.lmuDataPlugin - data file not saved. The following error occured: " + ex.Message);
            }
        }

        // <summary>
        // Return to simhub thee winform settings control here, return null if no settings control
        // 
        // </summary>
        // <param name="pluginManager"></param>
        // <returns></returns>
        public System.Windows.Forms.Control GetSettingsControl(PluginManager pluginManager)
        {
            return null;
        }

        // <summary>
        // Return to simhub thee winform settings control here, return null if no settings control
        // 
        // </summary>
        // <param name="pluginManager"></param>
        // <returns></returns>
        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            if (settingsControlwpf == null)
            {
                settingsControlwpf = new SettingsControl();
            }

            return settingsControlwpf;
        }

        /// <summary>
        /// Methode called by SimHub to refresh data. 
        /// 1. Calculate some "time data" from incoming data
        /// 2. Uppdate Simhub data value 
        /// </summary>
        /// <param name="pluginManager"></param>
        /// <param name="data"></param>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {

            curGame = data.GameName;
            GameInMenu = data.GameInMenu;
            GameRunning = data.GameRunning;
            GamePaused = data.GamePaused;
            GameReplay = data.GameReplay;

            // When game is in menu, we setup flag to recall the pitmenu settings at the beginning of the session
            if (curGame == "LMU"
                    && data.GameRunning && data.GameInMenu && (!loadSessionStaticInfoFromWS)
                ) 
            { 
                loadSessionStaticInfoFromWS = true;
            }

            // During game, we process simhub actions
            if (curGame == "LMU" //TODO: check a record where the game was captured from startup on
                    && data.GameRunning && !data.GameInMenu && !data.GamePaused && !data.GameReplay
                    && !StopUpdate && (data.OldData != null)
                )
            {
                //updateDataDelayCounter--;

                //****************** Process Incoming Data from SimHUB
                // global data
                LMURepairAndRefuelData.IsInPit = data.OldData.IsInPit;
                LMURepairAndRefuelData.CarClass = data.OldData.CarClass;
                LMURepairAndRefuelData.CarModel = data.OldData.CarModel;
                LMURepairAndRefuelData.BestLapTime = data.OldData.BestLapTime.TotalSeconds != 0 ? data.OldData.BestLapTime.TotalSeconds : data.OldData.AllTimeBest.TotalSeconds;

                double sessionTimeLeftInSeconds = data.OldData.SessionTimeLeft.TotalSeconds;
                LMURepairAndRefuelData.sessionTimeLeftInSeconds = sessionTimeLeftInSeconds;

                // Get fuelConsumption from DataCorePlugin (simhub plugin)
                var sh_fuelConsumption = pluginManager.GetPlugin<DataCorePlugin>();
                LMURepairAndRefuelData.sh_fuelConsumptionValue = (float)sh_fuelConsumption.properties.Computed_Fuel_LitersPerLap.Value;

                //***************** Detect Race Event from incoming data
                //detect out from pit
                if (data.OldData.IsInPit > data.NewData.IsInPit)
                {
                    OutFromPitFlag = true;
                    outFromPitTime = data.NewData.CurrentLapTime;
                }

                //detect in to pit
                if (data.OldData.IsInPit < data.NewData.IsInPit)
                {
                    InToPitFlag = true;
                    InToPitTime = data.NewData.CurrentLapTime;
                }

                //detect new lap
                if (data.OldData.CurrentLap < data.NewData.CurrentLap || (LMURepairAndRefuelData.energyPerLastLap == 0 && !updateConsuptionFlag))
                {
                    // Calculate last lap time
                    lastLapTime = (float)(sesstionTimeStamp - sessionTimeLeftInSeconds);
                    sesstionTimeStamp = sessionTimeLeftInSeconds;
                    // Calculate last lap time end

                    updateConsuptionFlag = true;
                    updateConsuptionDelayCounter = 10;

                    IsLapValid = data.OldData.IsLapValid;
                    LapInvalidated = data.OldData.LapInvalidated;
                }

                // detect new session
                if (data.OldData.SessionTypeName != data.NewData.SessionTypeName || data.OldData.IsSessionRestart != data.NewData.IsSessionRestart || !data.SessionId.Equals(SessionId))
                {
                    SessionId = data.SessionId;
                    lastLapTime = 0;
                    sesstionTimeStamp = sessionTimeLeftInSeconds;
                    LMURepairAndRefuelData.energyPerLastLap = 0;
                    LMURepairAndRefuelData.energyPerLast5Lap = 0;
                    LMURepairAndRefuelData.energyPerLast5ClearLap = 0;
                    LMURepairAndRefuelData.ComputedFuelRatio_LastLap = 0;
                    LMURepairAndRefuelData.ComputedFuelRatio_Last5Lap = 0;
                    EnergyConsuptions.Clear();
                    ClearEnergyConsuptions.Clear();
                    FuelConsuptions.Clear();
                    LapTimes.Clear();
                    FuelRatioAvg.Clear();
                }


                //**********  Update property in simhub with lastest values calculate
                if (NeedUpdateData)
                {
                    setPropertiesInSimhub(pluginManager);
                }

            }
        }

        private void setPropertiesInSimhub(PluginManager pluginManager) {
            try 
            {
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Batt.Current", this.GetType(), LMURepairAndRefuelData.currentBattery);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Batt.Current_%", this.GetType(), LMURepairAndRefuelData.currentBatteryP);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Batt.Max", this.GetType(), LMURepairAndRefuelData.maxBattery);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.ComputedFuelRatio_LastLap", this.GetType(), LMURepairAndRefuelData.ComputedFuelRatio_LastLap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.ComputedFuelRatio_PerLast5Lap", this.GetType(), LMURepairAndRefuelData.ComputedFuelRatio_Last5Lap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Fuel.Current_L", this.GetType(), LMURepairAndRefuelData.currentFuel);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Fuel.FractionPerLap_%", this.GetType(), LMURepairAndRefuelData.fuelFractionPerLap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Fuel.Max_L", this.GetType(), LMURepairAndRefuelData.maxFuel);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Fuel.NeededUntilEnd_L", this.GetType(), LMURepairAndRefuelData.FuelNeededUntilEnd);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Fuel.toRefill_L", this.GetType(), LMURepairAndRefuelData.FueltoRefill);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.Fuel.toRefill_safe_L", this.GetType(), LMURepairAndRefuelData.FueltoRefill_safe);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.Current_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergy);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.Current", this.GetType(), LMURepairAndRefuelData.currentVirtualEnergy);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.FractionPerLap_%", this.GetType(), LMURepairAndRefuelData.virtualEnergyFractionPerLap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.Max", this.GetType(), LMURepairAndRefuelData.maxVirtualEnergy);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.NeededUntilEnd_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergyNeededUntilEnd);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.PerLast5ClearLap_%", this.GetType(), LMURepairAndRefuelData.energyPerLast5ClearLap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.PerLast5Lap_%", this.GetType(), LMURepairAndRefuelData.energyPerLast5Lap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.PerLastLap_%", this.GetType(), LMURepairAndRefuelData.energyPerLastLap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.TimeElapsed", this.GetType(), LMURepairAndRefuelData.energyTimeElapsed);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.toRefill_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergytoRefill);
                pluginManager.SetPropertyValue("Redadeg.lmu.Energy.VE.toRefill_safe_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergytoRefill_safe);                            

                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.BrakeMigration", this.GetType(), LMURepairAndRefuelData.mpBrakeMigration);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.BrakeMigrationMax", this.GetType(), LMURepairAndRefuelData.mpBrakeMigrationMax);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.ChangedParamType", this.GetType(), LMURepairAndRefuelData.mChangedParamType);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.ChangedParamValue", this.GetType(), LMURepairAndRefuelData.mChangedParamValue);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.Cuts", this.GetType(), LMURepairAndRefuelData.Cuts);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.CutsMax", this.GetType(), LMURepairAndRefuelData.CutsMax);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.MotorMap", this.GetType(), LMURepairAndRefuelData.mpMotorMap);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.PenaltyCount", this.GetType(), LMURepairAndRefuelData.PenaltyCount);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.PenaltyLeftLaps", this.GetType(), LMURepairAndRefuelData.PenaltyLeftLaps);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.PenaltyType", this.GetType(), LMURepairAndRefuelData.PenaltyType);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.PendingPenaltyType1", this.GetType(), LMURepairAndRefuelData.mPendingPenaltyType1);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.PendingPenaltyType2", this.GetType(), LMURepairAndRefuelData.mPendingPenaltyType2);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.PendingPenaltyType3", this.GetType(), LMURepairAndRefuelData.mPendingPenaltyType3);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.TractionControl", this.GetType(), LMURepairAndRefuelData.mpTractionControl);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_ANTILOCKBRAKESYSTEMMAP", this.GetType(), LMURepairAndRefuelData.VM_ANTILOCKBRAKESYSTEMMAP);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_BRAKE_BALANCE", this.GetType(), LMURepairAndRefuelData.VM_BRAKE_BALANCE);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_BRAKE_MIGRATION", this.GetType(), LMURepairAndRefuelData.VM_BRAKE_MIGRATION);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_ELECTRIC_MOTOR_MAP", this.GetType(), LMURepairAndRefuelData.VM_ELECTRIC_MOTOR_MAP);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_ENGINE_BRAKEMAP", this.GetType(), LMURepairAndRefuelData.VM_ENGINE_BRAKEMAP);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_ENGINE_MIXTURE", this.GetType(), LMURepairAndRefuelData.VM_ENGINE_MIXTURE);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_FRONT_ANTISWAY", this.GetType(), LMURepairAndRefuelData.VM_FRONT_ANTISWAY);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_REAR_ANTISWAY", this.GetType(), LMURepairAndRefuelData.VM_REAR_ANTISWAY);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_REGEN_LEVEL", this.GetType(), LMURepairAndRefuelData.VM_REGEN_LEVEL);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_TRACTIONCONTROLMAP", this.GetType(), LMURepairAndRefuelData.VM_TRACTIONCONTROLMAP);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_TRACTIONCONTROLPOWERCUTMAP", this.GetType(), LMURepairAndRefuelData.VM_TRACTIONCONTROLPOWERCUTMAP);
                pluginManager.SetPropertyValue("Redadeg.lmu.Extended.VM_TRACTIONCONTROLSLIPANGLEMAP", this.GetType(), LMURepairAndRefuelData.VM_TRACTIONCONTROLSLIPANGLEMAP);

                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.isReplayActive", this.GetType(), LMURepairAndRefuelData.isReplayActive);
                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.MultiStintState", this.GetType(), LMURepairAndRefuelData.MultiStintState);
                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.PitEntryDist", this.GetType(), LMURepairAndRefuelData.PitEntryDist);
                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.PitState", this.GetType(), LMURepairAndRefuelData.PitState);
                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.RaceFinished", this.GetType(), LMURepairAndRefuelData.raceFinished);
                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.timeOfDay", this.GetType(), LMURepairAndRefuelData.timeOfDay);
                pluginManager.SetPropertyValue("Redadeg.lmu.GameInfos.lengthTime", this.GetType(), LMURepairAndRefuelData.lengthTime);

                pluginManager.SetPropertyValue("Redadeg.lmu.passStopAndGo", this.GetType(), LMURepairAndRefuelData.passStopAndGo);

                pluginManager.SetPropertyValue("Redadeg.lmu.Pit.MaxAvailableTires", this.GetType(), LMURepairAndRefuelData.maxAvailableTires);
                pluginManager.SetPropertyValue("Redadeg.lmu.Pit.NewTires", this.GetType(), LMURepairAndRefuelData.newTires);
                pluginManager.SetPropertyValue("Redadeg.lmu.Pit.StopLength", this.GetType(), LMURepairAndRefuelData.pitStopLength);

                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.FuelRatio", this.GetType(), LMURepairAndRefuelData.FuelRatio);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Grille", this.GetType(), LMURepairAndRefuelData.Grille);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.RepairDamage", this.GetType(), LMURepairAndRefuelData.RepairDamage);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.ReplaceBrakes", this.GetType(), LMURepairAndRefuelData.replaceBrakes);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_kPa);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_kPa_Text);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_Psi);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fl_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.fl_TyreChange_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_kPa);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_kPa_Text);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_Psi);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.fr_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.fr_TyreChange_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_kPa);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_kPa_Text);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_Psi);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rl_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.rl_TyreChange_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_kPa);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_kPa_Text);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_Psi);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Tyre.rr_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.rr_TyreChange_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Virtual_Energy", this.GetType(), LMURepairAndRefuelData.PitMVirtualEnergy);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Virtual_Energy_Text", this.GetType(), LMURepairAndRefuelData.PitMVirtualEnergy_Text);
                pluginManager.SetPropertyValue("Redadeg.lmu.PitMenu.Wing", this.GetType(), LMURepairAndRefuelData.Wing);

                pluginManager.SetPropertyValue("Redadeg.lmu.TeamInfos.Driver", this.GetType(), LMURepairAndRefuelData.Driver);
                pluginManager.SetPropertyValue("Redadeg.lmu.TeamInfos.TeamName", this.GetType(), LMURepairAndRefuelData.teamName);
                pluginManager.SetPropertyValue("Redadeg.lmu.TeamInfos.VehicleName", this.GetType(), LMURepairAndRefuelData.vehicleName);

                pluginManager.SetPropertyValue("Redadeg.lmu.TrackInfos.GrandPrixName", this.GetType(), LMURepairAndRefuelData.grandPrixName);
                pluginManager.SetPropertyValue("Redadeg.lmu.TrackInfos.Location", this.GetType(), LMURepairAndRefuelData.location);
                pluginManager.SetPropertyValue("Redadeg.lmu.TrackInfos.OpeningYear", this.GetType(), LMURepairAndRefuelData.openingYear);
                pluginManager.SetPropertyValue("Redadeg.lmu.TrackInfos.TrackLength", this.GetType(), LMURepairAndRefuelData.trackLength);
                pluginManager.SetPropertyValue("Redadeg.lmu.TrackInfos.TrackName", this.GetType(), LMURepairAndRefuelData.trackName);

                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fl_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.fl_TyreCompound_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fl_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.fl_TyrePressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fl_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.fl_TyrePressure);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fl_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.fl_TyrePressure_Psi);

                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fr_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.fr_TyreCompound_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fr_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.fr_TyrePressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fr_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.fr_TyrePressure);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.fr_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.fr_TyrePressure_Psi);

                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rl_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.rl_TyreCompound_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rl_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.rl_TyrePressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rl_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.rl_TyrePressure);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rl_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.rl_TyrePressure_Psi);

                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rr_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.rr_TyreCompound_Name);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rr_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.rr_TyrePressure_Bar);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rr_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.rr_TyrePressure);
                pluginManager.SetPropertyValue("Redadeg.lmu.Tyre.rr_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.rr_TyrePressure_Psi);

                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Current.AmbientTemp", this.GetType(), LMURepairAndRefuelData.ambientTemp);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Current.CloudCoverage_%", this.GetType(), LMURepairAndRefuelData.cloudCoverage);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Current.Humidity_%", this.GetType(), LMURepairAndRefuelData.humidity);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Current.LightLevel_%", this.GetType(), LMURepairAndRefuelData.lightLevel);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Current.Raining_%", this.GetType(), LMURepairAndRefuelData.raining);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Current.RainIntensity_%", this.GetType(), LMURepairAndRefuelData.rainIntensity);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.RainChance_%", this.GetType(), LMURepairAndRefuelData.rainChance);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Track.Temp", this.GetType(), LMURepairAndRefuelData.trackTemp);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Track.Wetness_%", this.GetType(), LMURepairAndRefuelData.trackWetness);
                pluginManager.SetPropertyValue("Redadeg.lmu.WeatherInfos.Track.Wetness_Text", this.GetType(), LMURepairAndRefuelData.trackWetness_Text);

                // when data are update we change the status to precent cpu usage at the next simhub call, if no data are updated
                NeedUpdateData = false;
            }
            catch (Exception ex)
            {
                Logging.Current.Info("Plugin Redadeg.lmuDataPlugin Update parameters: " + ex.ToString());
            }
        }

        private async void lmu_CalculateConsumptionsThread()
        {
            try
            {
                await Task.Delay(500, ctsCalculateConsumptionsThread.Token);
                while (!IsEnded)
                {
                    if (GameRunning && !GameInMenu && !GamePaused && !GameReplay && curGame == "LMU")
                    {
                        if (updateConsuptionFlag)
                        {
                            if (updateConsuptionDelayCounter < 0)
                            {
                                JObject TireMagagementJSONdata = JObject.Parse(await FetchTireManagementJSONdata());
                                JObject expectedUsage = JObject.Parse(TireMagagementJSONdata["expectedUsage"].ToString());

                                float fuelConsumption = (float)expectedUsage["fuelConsumption"];
                                float fuelFractionPerLap = (float)expectedUsage["fuelFractionPerLap"];
                                float virtualEnergyConsumption = (float)((float)expectedUsage["virtualEnergyConsumption"] / (float)LMURepairAndRefuelData.maxVirtualEnergy * 100);
                                float virtualEnergyFractionPerLap = (float)expectedUsage["virtualEnergyFractionPerLap"];
                                float ComputedFuelRatio_LastLap = fuelFractionPerLap / virtualEnergyFractionPerLap;

                                LMURepairAndRefuelData.fuelFractionPerLap = (float)Math.Round((fuelFractionPerLap * 100), 4);
                                LMURepairAndRefuelData.virtualEnergyFractionPerLap = (float)Math.Round((virtualEnergyFractionPerLap * 100), 4);
                                LMURepairAndRefuelData.energyPerLastLap = (float)Math.Round(virtualEnergyConsumption, 2);
                                LMURepairAndRefuelData.ComputedFuelRatio_LastLap = (float)Math.Round((ComputedFuelRatio_LastLap), 2);

                                if (EnergyConsuptions.Count < 5)
                                {
                                    energy_CurrentIndex++;
                                    EnergyConsuptions.Add(virtualEnergyConsumption);
                                    fuelratio_CurrentIndex++;
                                    FuelRatioAvg.Add(ComputedFuelRatio_LastLap);
                                }
                                else if (EnergyConsuptions.Count == 5)
                                {
                                    energy_CurrentIndex++;
                                    if (energy_CurrentIndex > 4) energy_CurrentIndex = 0;
                                    EnergyConsuptions[energy_CurrentIndex] = virtualEnergyConsumption;
                                    fuelratio_CurrentIndex++;
                                    if (fuelratio_CurrentIndex > 4) fuelratio_CurrentIndex = 0;
                                    FuelRatioAvg[fuelratio_CurrentIndex] = ComputedFuelRatio_LastLap;
                                }

                                if (IsLapValid && !LapInvalidated && !OutFromPitFlag && !InToPitFlag && LMURepairAndRefuelData.IsInPit == 0)
                                {
                                    if (LapTimes.Count < 5)
                                    {
                                        energy_CurrentIndex++;                                        
                                        ClearEnergyConsuptions.Add(virtualEnergyConsumption);
                                        FuelConsuptions.Add(fuelConsumption);
                                        LapTimes.Add((float)lastLapTime);
                                    }
                                    else if (LapTimes.Count == 5)
                                    {
                                        energy_CurrentIndex++;
                                        if (energy_CurrentIndex > 4) energy_CurrentIndex = 0;
                                        LapTimes[energy_CurrentIndex] = (float)lastLapTime;
                                        ClearEnergyConsuptions[energy_CurrentIndex] = virtualEnergyConsumption;
                                        FuelConsuptions[energy_CurrentIndex] = fuelConsumption;
                                    }
                                }
                                updateConsuptionFlag = false;
                                updateConsuptionDelayCounter = 10;
                            }
                        // Logging.Current.Info("Last Lap: " + lastLapTime.ToString() + " updateConsuptionDelayCounter: " + updateConsuptionDelayCounter.ToString() + " virtualEnergyConsumption: " + virtualEnergyConsumption.ToString());

                        updateConsuptionDelayCounter--;
                    }
                    OutFromPitFlag = false;
                    InToPitFlag = false;
                }
                await Task.Delay(ButtonBindSettings.ConsUpdateThreadTimeout, ctsCalculateConsumptionsThread.Token);
            }
            }
            catch (AggregateException)
            {
                Logging.Current.Info(("AggregateException"));
            }
            catch (TaskCanceledException)
            {
                Logging.Current.Info(("TaskCanceledException"));
            }
        }

        private async void lmu_GetJSonDataThread()
            {
            try
            {
                await Task.Delay(500, ctsGetJSonDataThread.Token);
                while (!IsEnded)
                {

                    if (GameRunning && !GameInMenu && !GamePaused && !GameReplay && curGame == "LMU")
                    {

                        // Load data from the webservice once when exiting menu
                        try
                        {
                            if (loadSessionStaticInfoFromWS)
                            {
                                JObject SetupJSONdata = JObject.Parse(await FetchCarSetupOverviewJSONdata());
                                JObject garageValues = JObject.Parse(SetupJSONdata["carSetup"]?["garageValues"].ToString());

                                LMURepairAndRefuelData.VM_ANTILOCKBRAKESYSTEMMAP = garageValues["VM_ANTILOCKBRAKESYSTEMMAP"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_BRAKE_BALANCE = garageValues["VM_BRAKE_BALANCE"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_BRAKE_MIGRATION = garageValues["VM_BRAKE_MIGRATION"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_ENGINE_BRAKEMAP = garageValues["VM_ENGINE_BRAKEMAP"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_ELECTRIC_MOTOR_MAP = garageValues["VM_ELECTRIC_MOTOR_MAP"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_ENGINE_MIXTURE = garageValues["VM_ENGINE_MIXTURE"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_REGEN_LEVEL = garageValues["VM_REGEN_LEVEL"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_TRACTIONCONTROLMAP = garageValues["VM_TRACTIONCONTROLMAP"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_TRACTIONCONTROLPOWERCUTMAP = garageValues["VM_TRACTIONCONTROLPOWERCUTMAP"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_TRACTIONCONTROLSLIPANGLEMAP = garageValues["VM_TRACTIONCONTROLSLIPANGLEMAP"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_REAR_ANTISWAY = garageValues["VM_REAR_ANTISWAY"]?["stringValue"].ToString();
                                LMURepairAndRefuelData.VM_FRONT_ANTISWAY = garageValues["VM_FRONT_ANTISWAY"]?["stringValue"].ToString();
                                // Garage data are loaded, we don't need to reload them until we go in GarageMenu
                                
                                await Task.Delay(ButtonBindSettings.DataUpdateThreadTimeout, ctsGetJSonDataThread.Token);

                                // race info don't change during the session, we load it once when we leave garage
                                JObject InfoForEventJSONdata = JObject.Parse(await FetchInfoForEventJSONdata());
                                JObject scheduledSessions = JObject.Parse(InfoForEventJSONdata.ToString());

                                foreach (JObject Sesstions in scheduledSessions["scheduledSessions"])
                                {
                                    if (Sesstions["name"].ToString().ToUpper().Equals(LMURepairAndRefuelData.SessionTypeName)) LMURepairAndRefuelData.rainChance = (int)Sesstions["rainChance"];
                                }

                                loadSessionStaticInfoFromWS = false;
                            }
                        }
                        catch
                        {

                        }

                        // Start New Datas 04-2025
                        try
                        {
                            // call LMMU Webservice and wait to calm down the api throttle and fix Menu Flickering
                            JObject TireMagagementJSONdata = JObject.Parse(await FetchTireManagementJSONdata());    // busy request, need waiting
                            await Task.Delay(ButtonBindSettings.DataUpdateThreadTimeout, ctsGetJSonDataThread.Token);

                            JObject RepairAndRefuelJSONdata = JObject.Parse(await FetchRepairAndRefuelJSONdata());  // busy request, need waiting
                            await Task.Delay(ButtonBindSettings.DataUpdateThreadTimeout, ctsGetJSonDataThread.Token);

                            JObject GameStateJSONdata = JObject.Parse(await FetchGetGameStateJSONdata());
                            await Task.Delay(ButtonBindSettings.DataUpdateThreadTimeout, ctsGetJSonDataThread.Token);

                            JObject RaceHistoryJSONdata = JObject.Parse(await FetchRaceHistoryJSONdata());
                            await Task.Delay(ButtonBindSettings.DataUpdateThreadTimeout, ctsGetJSonDataThread.Token);

                            JArray pitMenuJSONData = JArray.Parse(await FetchPitMenuJSONdata());                    // busy request, need waiting
                            // Not need to wait here, it's the last call and we wait in the loop

                            JObject tireInventory = JObject.Parse(TireMagagementJSONdata["tireInventory"].ToString());

                            if (pitStopUpdatePause == -1)
                            {
                                pitMenuH = JObject.Parse(RepairAndRefuelJSONdata["pitMenu"].ToString());
                            }
                            else
                            {
                                if (pitStopUpdatePause == 0) // Update pit data if pitStopUpdatePauseCounter is 0
                                {
                                    pitStopUpdatePause = -1;
                                }
                                pitStopUpdatePause--;
                            }                            

                            LMURepairAndRefuelData.maxAvailableTires = (int)tireInventory["maxAvailableTires"];
                            LMURepairAndRefuelData.newTires = (int)tireInventory["newTires"];
                            LMURepairAndRefuelData.pitStopLength = (int)RepairAndRefuelJSONdata["pitStopLength"]?["timeInSeconds"];

                            // Start Pit Menu

                            String RepairDamageText = GetPMCText(pitMenuJSONData, 1, "Unknown");

                            if (RepairDamageText != null)
                            {
                                string rawText = RepairDamageText?.ToString() ?? "Unknown";

                                byte[] utf8Bytes = Encoding.Default.GetBytes(rawText);
                                string utf8Text = Encoding.UTF8.GetString(utf8Bytes);

                                LMURepairAndRefuelData.RepairDamage = utf8Text.Normalize(NormalizationForm.FormC);
                            }
                            else
                            {
                                LMURepairAndRefuelData.RepairDamage = "Unknown";
                            }

                            string fuelRatioText = GetPMCText(pitMenuJSONData, 6, "1.0");
                            if (float.TryParse(fuelRatioText, out float fuelRatio))
                            {
                                LMURepairAndRefuelData.FuelRatio = fuelRatio;
                            }
                            else
                            {
                                LMURepairAndRefuelData.FuelRatio = 1;
                            }

                            LMURepairAndRefuelData.PitMVirtualEnergy = GetPMCValue(pitMenuJSONData, 5);
                            LMURepairAndRefuelData.PitMVirtualEnergy_Text = GetPMCText(pitMenuJSONData, 5, "Unknown");                                    
                            LMURepairAndRefuelData.Wing = GetPMCText(pitMenuJSONData, 19, "0");
                            LMURepairAndRefuelData.Grille = GetPMCText(pitMenuJSONData, 21, "Unknown");
                            LMURepairAndRefuelData.replaceBrakes = GetPMCText(pitMenuJSONData, 32, "Unknown");
                            LMURepairAndRefuelData.fl_Tyre_NewPressure_kPa_Text = GetPMCText(pitMenuJSONData, 24, "Unknown");
                            LMURepairAndRefuelData.fr_Tyre_NewPressure_kPa_Text = GetPMCText(pitMenuJSONData, 25, "Unknown");
                            LMURepairAndRefuelData.rl_Tyre_NewPressure_kPa_Text = GetPMCText(pitMenuJSONData, 26, "Unknown");
                            LMURepairAndRefuelData.rr_Tyre_NewPressure_kPa_Text = GetPMCText(pitMenuJSONData, 27, "Unknown");

                            // Start PMC Pressure

                            SetTyrePressureData(pitMenuJSONData, 24, out int flPressure_kPa, out float flPressure_Bar, out float flPressure_Psi);
                            LMURepairAndRefuelData.fl_Tyre_NewPressure_kPa = flPressure_kPa;
                            LMURepairAndRefuelData.fl_Tyre_NewPressure_Bar = flPressure_Bar;
                            LMURepairAndRefuelData.fl_Tyre_NewPressure_Psi = flPressure_Psi;

                            SetTyrePressureData(pitMenuJSONData, 25, out int frPressure_kPa, out float frPressure_Bar, out float frPressure_Psi);
                            LMURepairAndRefuelData.fr_Tyre_NewPressure_kPa = frPressure_kPa;
                            LMURepairAndRefuelData.fr_Tyre_NewPressure_Bar = frPressure_Bar;
                            LMURepairAndRefuelData.fr_Tyre_NewPressure_Psi = frPressure_Psi;
                                
                            SetTyrePressureData(pitMenuJSONData, 26, out int rlPressure_kPa, out float rlPressure_Bar, out float rlPressure_Psi);
                            LMURepairAndRefuelData.rl_Tyre_NewPressure_kPa = rlPressure_kPa;
                            LMURepairAndRefuelData.rl_Tyre_NewPressure_Bar = rlPressure_Bar;
                            LMURepairAndRefuelData.rl_Tyre_NewPressure_Psi = rlPressure_Psi;

                            SetTyrePressureData(pitMenuJSONData, 27, out int rrPressure_kPa, out float rrPressure_Bar, out float rrPressure_Psi);
                            LMURepairAndRefuelData.rr_Tyre_NewPressure_kPa = rrPressure_kPa;
                            LMURepairAndRefuelData.rr_Tyre_NewPressure_Bar = rrPressure_Bar;
                            LMURepairAndRefuelData.rr_Tyre_NewPressure_Psi = rrPressure_Psi;
                                
                            // End PMC Pressure
                            // End Pit Menu

                            // Start New datas for each tyre                                
                            var selectedDatas = TireMagagementJSONdata["wheelInfo"]["wheelLocs"].ToList();

                            //Start Compound
                            if (LMURepairAndRefuelData.trackName == "Circuit de la Sarthe" || LMURepairAndRefuelData.trackName == "Circuit de Spa-Francorchamps")
                            {
                                LMURepairAndRefuelData.fl_TyreCompound_Name = selectedDatas[0]["compound"] != null ? (int)selectedDatas[0]["compound"] == 0 ? "Soft" : (int)selectedDatas[0]["compound"] == 1 ? "Medium" : (int)selectedDatas[0]["compound"] == 2 ? "Hard" : (int)selectedDatas[0]["compound"] == 3 ? "Wet" : $"{(int)selectedDatas[0]["compound"]}" : "0";
                                LMURepairAndRefuelData.fr_TyreCompound_Name = selectedDatas[1]["compound"] != null ? (int)selectedDatas[1]["compound"] == 0 ? "Soft" : (int)selectedDatas[1]["compound"] == 1 ? "Medium" : (int)selectedDatas[1]["compound"] == 2 ? "Hard" : (int)selectedDatas[1]["compound"] == 3 ? "Wet" : $"{(int)selectedDatas[1]["compound"]}" : "0";
                                LMURepairAndRefuelData.rl_TyreCompound_Name = selectedDatas[2]["compound"] != null ? (int)selectedDatas[2]["compound"] == 0 ? "Soft" : (int)selectedDatas[2]["compound"] == 1 ? "Medium" : (int)selectedDatas[2]["compound"] == 2 ? "Hard" : (int)selectedDatas[2]["compound"] == 3 ? "Wet" : $"{(int)selectedDatas[2]["compound"]}" : "0";
                                LMURepairAndRefuelData.rr_TyreCompound_Name = selectedDatas[3]["compound"] != null ? (int)selectedDatas[3]["compound"] == 0 ? "Soft" : (int)selectedDatas[3]["compound"] == 1 ? "Medium" : (int)selectedDatas[3]["compound"] == 2 ? "Hard" : (int)selectedDatas[3]["compound"] == 3 ? "Wet" : $"{(int)selectedDatas[3]["compound"]}" : "0";
                            }

                            else
                            {
                                LMURepairAndRefuelData.fl_TyreCompound_Name = selectedDatas[0]["compound"] != null ? (int)selectedDatas[0]["compound"] == 0 ? "Medium" : (int)selectedDatas[0]["compound"] == 1 ? "Hard" : (int)selectedDatas[0]["compound"] == 2 ? "Wet" : $"{(int)selectedDatas[0]["compound"]}" : "0";
                                LMURepairAndRefuelData.fr_TyreCompound_Name = selectedDatas[1]["compound"] != null ? (int)selectedDatas[1]["compound"] == 0 ? "Medium" : (int)selectedDatas[1]["compound"] == 1 ? "Hard" : (int)selectedDatas[1]["compound"] == 2 ? "Wet" : $"{(int)selectedDatas[1]["compound"]}" : "0";
                                LMURepairAndRefuelData.rl_TyreCompound_Name = selectedDatas[2]["compound"] != null ? (int)selectedDatas[2]["compound"] == 0 ? "Medium" : (int)selectedDatas[2]["compound"] == 1 ? "Hard" : (int)selectedDatas[2]["compound"] == 2 ? "Wet" : $"{(int)selectedDatas[2]["compound"]}" : "0";
                                LMURepairAndRefuelData.rr_TyreCompound_Name = selectedDatas[3]["compound"] != null ? (int)selectedDatas[3]["compound"] == 0 ? "Medium" : (int)selectedDatas[3]["compound"] == 1 ? "Hard" : (int)selectedDatas[3]["compound"] == 2 ? "Wet" : $"{(int)selectedDatas[3]["compound"]}" : "0";
                            }
                            // End Compound

                            // Start New Tyre Change
                            int fl_tirechange = (int)GetPMCValue(pitMenuJSONData, 12);
                            int fr_tirechange = (int)GetPMCValue(pitMenuJSONData, 13);
                            int rl_tirechange = (int)GetPMCValue(pitMenuJSONData, 14);
                            int rr_tirechange = (int)GetPMCValue(pitMenuJSONData, 15);

                            if (LMURepairAndRefuelData.trackName == "Circuit de la Sarthe" || LMURepairAndRefuelData.trackName == "Circuit de Spa-Francorchamps")
                            {
                                LMURepairAndRefuelData.fl_TyreChange_Name = fl_tirechange == 0 ? "No Change" : fl_tirechange == 1 ? "New Soft" : fl_tirechange == 2 ? "New Medium" : fl_tirechange == 3 ? "New Hard" : fl_tirechange == 4 ? "New Wet" : fl_tirechange == 5 ? "Use Soft" : fl_tirechange == 6 ? "Use Medium" : fl_tirechange == 7 ? "Use Hard" : fl_tirechange == 8 ? "Use Wet" : $"{fl_tirechange}";
                                LMURepairAndRefuelData.fr_TyreChange_Name = fr_tirechange == 0 ? "No Change" : fr_tirechange == 1 ? "New Soft" : fr_tirechange == 2 ? "New Medium" : fr_tirechange == 3 ? "New Hard" : fr_tirechange == 4 ? "New Wet" : fr_tirechange == 5 ? "Use Soft" : fr_tirechange == 6 ? "Use Medium" : fr_tirechange == 7 ? "Use Hard" : fr_tirechange == 8 ? "Use Wet" : $"{fr_tirechange}";
                                LMURepairAndRefuelData.rl_TyreChange_Name = rl_tirechange == 0 ? "No Change" : rl_tirechange == 1 ? "New Soft" : rl_tirechange == 2 ? "New Medium" : rl_tirechange == 3 ? "New Hard" : rl_tirechange == 4 ? "New Wet" : rl_tirechange == 5 ? "Use Soft" : rl_tirechange == 6 ? "Use Medium" : rl_tirechange == 7 ? "Use Hard" : rl_tirechange == 8 ? "Use Wet" : $"{rl_tirechange}";
                                LMURepairAndRefuelData.rr_TyreChange_Name = rr_tirechange == 0 ? "No Change" : rr_tirechange == 1 ? "New Soft" : rr_tirechange == 2 ? "New Medium" : rr_tirechange == 3 ? "New Hard" : rr_tirechange == 4 ? "New Wet" : rr_tirechange == 5 ? "Use Soft" : rr_tirechange == 6 ? "Use Medium" : rr_tirechange == 7 ? "Use Hard" : rr_tirechange == 8 ? "Use Wet" : $"{rr_tirechange}";
                            }
                            else
                            {
                                LMURepairAndRefuelData.fl_TyreChange_Name = fl_tirechange == 0 ? "No Change" : fl_tirechange == 1 ? "New Medium" : fl_tirechange == 2 ? "New Hard" : fl_tirechange == 3 ? "New Wet" : fl_tirechange == 4 ? "Use Medium" : fl_tirechange == 5 ? "Use Hard" : fl_tirechange == 6 ? "Use Wet" : $"{fl_tirechange}";
                                LMURepairAndRefuelData.fr_TyreChange_Name = fr_tirechange == 0 ? "No Change" : fr_tirechange == 1 ? "New Medium" : fr_tirechange == 2 ? "New Hard" : fr_tirechange == 3 ? "New Wet" : fr_tirechange == 4 ? "Use Medium" : fr_tirechange == 5 ? "Use Hard" : fr_tirechange == 6 ? "Use Wet" : $"{fr_tirechange}";
                                LMURepairAndRefuelData.rl_TyreChange_Name = rl_tirechange == 0 ? "No Change" : rl_tirechange == 1 ? "New Medium" : rl_tirechange == 2 ? "New Hard" : rl_tirechange == 3 ? "New Wet" : rl_tirechange == 4 ? "Use Medium" : rl_tirechange == 5 ? "Use Hard" : rl_tirechange == 6 ? "Use Wet" : $"{rl_tirechange}";
                                LMURepairAndRefuelData.rr_TyreChange_Name = rr_tirechange == 0 ? "No Change" : rr_tirechange == 1 ? "New Medium" : rr_tirechange == 2 ? "New Hard" : rr_tirechange == 3 ? "New Wet" : rr_tirechange == 4 ? "Use Medium" : rr_tirechange == 5 ? "Use Hard" : rr_tirechange == 6 ? "Use Wet" : $"{rr_tirechange}";
                            }
                            // End New Tyre Change

                            // Start Tyre Pressure, Temperature and Brake Temperature                            
                            float fltyrepressure = (float)selectedDatas[0]["tirePressure"];
                            float frtyrepressure = (float)selectedDatas[1]["tirePressure"];
                            float rltyrepressure = (float)selectedDatas[2]["tirePressure"];
                            float rrtyrepressure = (float)selectedDatas[3]["tirePressure"];

                            LMURepairAndRefuelData.fl_TyrePressure = (int)Math.Round(fltyrepressure, 0);
                            LMURepairAndRefuelData.fr_TyrePressure = (int)Math.Round(frtyrepressure, 0);
                            LMURepairAndRefuelData.rl_TyrePressure = (int)Math.Round(rltyrepressure, 0);
                            LMURepairAndRefuelData.rr_TyrePressure = (int)Math.Round(rrtyrepressure, 0);

                            LMURepairAndRefuelData.fl_TyrePressure_Bar = (float)Math.Round(fltyrepressure / 100, 2);
                            LMURepairAndRefuelData.fr_TyrePressure_Bar = (float)Math.Round(frtyrepressure / 100, 2);
                            LMURepairAndRefuelData.rl_TyrePressure_Bar = (float)Math.Round(rltyrepressure / 100, 2);
                            LMURepairAndRefuelData.rr_TyrePressure_Bar = (float)Math.Round(rrtyrepressure / 100, 2);

                            LMURepairAndRefuelData.fl_TyrePressure_Psi = (float)Math.Round(fltyrepressure * PsiConvert, 2);
                            LMURepairAndRefuelData.fr_TyrePressure_Psi = (float)Math.Round(frtyrepressure * PsiConvert, 2);
                            LMURepairAndRefuelData.rl_TyrePressure_Psi = (float)Math.Round(rltyrepressure * PsiConvert, 2);
                            LMURepairAndRefuelData.rr_TyrePressure_Psi = (float)Math.Round(rrtyrepressure * PsiConvert, 2);                            
                                
                            // End Tyre Pressure, Temperature and Brake Temperature
                            // End New datas for each tyre

                            // Start Track & Team infos                                
                            JObject trackInfo = JObject.Parse(RaceHistoryJSONdata["trackInfo"].ToString());

                            LMURepairAndRefuelData.grandPrixName = trackInfo["grandPrixName"] != null ? (string)trackInfo["grandPrixName"] : "Unknown";
                            LMURepairAndRefuelData.location = trackInfo["location"] != null ? (string)trackInfo["location"] : "Unknown";
                            LMURepairAndRefuelData.openingYear = trackInfo["openingYear"] != null ? (string)trackInfo["openingYear"] : "Unknown";
                            LMURepairAndRefuelData.trackLength = trackInfo["trackLength"] != null ? $"{(string)trackInfo["trackLength"]} Kms" : "Unknown";
                            LMURepairAndRefuelData.trackName = trackInfo["trackName"] != null ? (string)trackInfo["trackName"] : "Unknown";

                            LMURepairAndRefuelData.Driver = RaceHistoryJSONdata["standings"]?["vehiclesInOrder"]?[0]?["driverName"] != null ? (string)RaceHistoryJSONdata["standings"]?["vehiclesInOrder"]?[0]?["driverName"] : "Unknown";
                            LMURepairAndRefuelData.teamName = RaceHistoryJSONdata["standings"]?["vehiclesInOrder"]?[0]?["teamName"] != null ? (string)RaceHistoryJSONdata["standings"]?["vehiclesInOrder"]?[0]?["teamName"] : "Unknown";
                            LMURepairAndRefuelData.vehicleName = RaceHistoryJSONdata["standings"]?["vehiclesInOrder"]?[0]?["vehicleName"] != null ? (string)RaceHistoryJSONdata["standings"]?["vehiclesInOrder"]?[0]?["vehicleName"] : "Unknown";
                                
                            // End Track & Team infos

                            // Start Game infos                                
                            LMURepairAndRefuelData.MultiStintState = GameStateJSONdata["MultiStintState"] != null ? (string)GameStateJSONdata["MultiStintState"] : "Unknown";
                            LMURepairAndRefuelData.PitEntryDist = (float)Math.Round((float)GameStateJSONdata["PitEntryDist"], 2);
                            LMURepairAndRefuelData.PitState = GameStateJSONdata["PitState"] != null ? (string)GameStateJSONdata["PitState"] : "Unknown";
                            LMURepairAndRefuelData.isReplayActive = GameStateJSONdata["isReplayActive"] != null ? (string)GameStateJSONdata["isReplayActive"] : "Unknown";
                            LMURepairAndRefuelData.raceFinished = GameStateJSONdata["raceFinished"] != null ? (string)GameStateJSONdata["raceFinished"] : "Unknown";
                            LMURepairAndRefuelData.timeOfDay = TimeSpan.FromSeconds((float)GameStateJSONdata["timeOfDay"]).ToString(@"hh\:mm");  //(@"hh\:mm\:ss\:fff")

                            // R�cup�re la valeur de "currentSession"
                            JObject eventInfo = JObject.Parse(RaceHistoryJSONdata["eventInfo"].ToString());
                            string currentSession = eventInfo?["currentSession"].ToString();

                            // Parcourt les sessions planifi�es pour trouver celle qui correspond
                            foreach (var session in eventInfo["info"]["scheduledSessions"])
                            {
                                if (session["name"] != null && session["name"].ToString().Equals(currentSession, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Retourne la valeur de "lengthTime" si trouv�e
                                    LMURepairAndRefuelData.lengthTime = session["lengthTime"] != null ? (int)session["lengthTime"] : 0;
                                }
                            }

                            // End Game Infos

                            // Start Actual Weather Infos
                            JObject currentWeather = JObject.Parse(TireMagagementJSONdata["currentWeather"].ToString());

                            LMURepairAndRefuelData.ambientTemp = (float)Math.Round((float)currentWeather["ambientTempKelvin"] - zeroKelvin, 1);
                            LMURepairAndRefuelData.cloudCoverage = (float)Math.Round((float)currentWeather["cloudCoverage"] * 100, 2);
                            LMURepairAndRefuelData.humidity = (float)Math.Round((float)currentWeather["humidity"] * 100, 2);
                            LMURepairAndRefuelData.lightLevel = (float)Math.Round((float)currentWeather["lightLevel"] * 100, 2);
                            LMURepairAndRefuelData.rainIntensity = (float)Math.Round((float)currentWeather["rainIntensity"] * 100, 2);
                            LMURepairAndRefuelData.raining = (float)Math.Round((float)currentWeather["raining"] * 100, 2);

                            // End Actual Weather Infos

                            // Start Track Condition                                
                            LMURepairAndRefuelData.trackTemp = RaceHistoryJSONdata["trackCondition"]?["trackTemp"] != null ? (float)Math.Round((float)RaceHistoryJSONdata["trackCondition"]?["trackTemp"] - zeroKelvin, 1) : 0;
                            float trackWetness = RaceHistoryJSONdata["trackCondition"]?["trackWetness"] != null ? (float)RaceHistoryJSONdata["trackCondition"]?["trackWetness"] * 100 : 0;

                            LMURepairAndRefuelData.trackWetness = (float)Math.Round(trackWetness, 2);

                            if (trackWetness <= 0.5)
                                LMURepairAndRefuelData.trackWetness_Text = "Dry";
                            else if (trackWetness <= 7.5)
                                LMURepairAndRefuelData.trackWetness_Text = "Damp";
                            else if (trackWetness <= 17.5)
                                LMURepairAndRefuelData.trackWetness_Text = "Slightly wet";
                            else if (trackWetness <= 40)
                                LMURepairAndRefuelData.trackWetness_Text = "Wet";
                            else if (trackWetness <= 70)
                                LMURepairAndRefuelData.trackWetness_Text = "Very wet";
                            else if (trackWetness <= 100)
                                LMURepairAndRefuelData.trackWetness_Text = "Extremely wet";
                                
                            // End Track Condition

                            // Start Virtual Energy management                                
                            JObject fuelInfo = JObject.Parse(RepairAndRefuelJSONdata["fuelInfo"].ToString());

                            double BestLapTime = LMURepairAndRefuelData.BestLapTime;
                            double sessionTimeLeftInSeconds = LMURepairAndRefuelData.sessionTimeLeftInSeconds;
                            double sh_fuelConsumptionValue = LMURepairAndRefuelData.sh_fuelConsumptionValue;

                            float currentFuel = (float)Math.Round((float)fuelInfo["currentFuel"], 1);
                            int maxFuel = (int)fuelInfo["maxFuel"];
                            float FuelRatio = LMURepairAndRefuelData.FuelRatio != 0 ? LMURepairAndRefuelData.FuelRatio : 1;

                            int currentVirtualEnergy = (int)fuelInfo["currentVirtualEnergy"];
                            int maxVirtualEnergy = (int)fuelInfo["maxVirtualEnergy"];
                            float virtualEnergyFractionPerLap = LMURepairAndRefuelData.virtualEnergyFractionPerLap != 0 ? LMURepairAndRefuelData.virtualEnergyFractionPerLap : 1;
                            float virtualErg = (float)Math.Round((currentVirtualEnergy / (double)maxVirtualEnergy) * 100, 1);

                            double FuelNeededUntilEnd = sessionTimeLeftInSeconds / BestLapTime * sh_fuelConsumptionValue;
                            double FueltoRefill = FuelNeededUntilEnd - currentFuel;
                            double FueltoRefill_safe = FueltoRefill + sh_fuelConsumptionValue;

                            double VirtualEnergyNeededUntilEnd = FuelNeededUntilEnd / FuelRatio;
                            double VirtualEnergytoRefill = VirtualEnergyNeededUntilEnd - virtualErg;
                            double VirtualEnergytoRefill_safe = VirtualEnergytoRefill + virtualEnergyFractionPerLap;

                            //LMURepairAndRefuelData.sh_fuelConsumptionValue = (float)Math.Round(sh_fuelConsumptionValue, 2);

                            LMURepairAndRefuelData.currentBattery = (int)fuelInfo["currentBattery"];                            
                            LMURepairAndRefuelData.maxBattery = (int)fuelInfo["maxBattery"];
                            if (LMURepairAndRefuelData.maxBattery != 0) {
                                LMURepairAndRefuelData.currentBatteryP = 100 * LMURepairAndRefuelData.currentBattery / LMURepairAndRefuelData.maxBattery;
                            } else
                            {
                                LMURepairAndRefuelData.currentBatteryP = 0;
                            }

                                LMURepairAndRefuelData.currentFuel = currentFuel;
                            LMURepairAndRefuelData.maxFuel = maxFuel;

                            LMURepairAndRefuelData.FuelNeededUntilEnd = (float)Math.Round((FuelNeededUntilEnd), 1);
                            LMURepairAndRefuelData.FueltoRefill = currentFuel >= FuelNeededUntilEnd ? 0 : (float)Math.Round(Math.Min(FueltoRefill, maxFuel), 1);
                            LMURepairAndRefuelData.FueltoRefill_safe = currentFuel >= FuelNeededUntilEnd ? 0 : (float)Math.Round(Math.Min(FueltoRefill_safe, maxFuel), 1);

                            LMURepairAndRefuelData.maxVirtualEnergy = maxVirtualEnergy;
                            LMURepairAndRefuelData.currentVirtualEnergy = currentVirtualEnergy;
                            LMURepairAndRefuelData.VirtualEnergy = virtualErg;

                            LMURepairAndRefuelData.VirtualEnergyNeededUntilEnd = (float)Math.Round((VirtualEnergyNeededUntilEnd), 0);
                            LMURepairAndRefuelData.VirtualEnergytoRefill = virtualErg >= VirtualEnergyNeededUntilEnd ? 0 : (float)Math.Round(Math.Min(VirtualEnergytoRefill, 100), 0);
                            LMURepairAndRefuelData.VirtualEnergytoRefill_safe = virtualErg >= VirtualEnergyNeededUntilEnd ? 0 : (float)Math.Round(Math.Min(VirtualEnergytoRefill_safe, 100), 0);

                            LMURepairAndRefuelData.energyTimeElapsed = ClearEnergyConsuptions.Count() > 0 && LapTimes.Count() > 0 && maxVirtualEnergy > 0 ? (float)Math.Round((LapTimes.Average() * virtualErg / ClearEnergyConsuptions.Average()) / 60, 1): 0;
                            LMURepairAndRefuelData.energyPerLast5Lap = EnergyConsuptions.Count() > 0 ? (float)Math.Round(EnergyConsuptions.Average(), 2) : 0;
                            LMURepairAndRefuelData.energyPerLast5ClearLap = ClearEnergyConsuptions.Count() > 0 ? (float)Math.Round(ClearEnergyConsuptions.Average(), 2) : 0;
                            LMURepairAndRefuelData.ComputedFuelRatio_Last5Lap = FuelRatioAvg.Count() > 0 ? (float)Math.Round(FuelRatioAvg.Average(), 2) : 0;
                            // End Virtual Energy management

                        }
                        catch (Exception ex)
                        {
                            Logging.Current.Error($"LMU Redadeg plugin : Unexpected error" + ex.ToString());
                            Logging.Current.Info("SectorChange: " + ex.ToString());
                        }
                        // End New Datas 04-2025
                        NeedUpdateData = true;
                    }
                    await Task.Delay(ButtonBindSettings.DataUpdateThreadTimeout, ctsGetJSonDataThread.Token);
                }
            }
            catch (AggregateException)
            {
                Logging.Current.Info(("AggregateException"));
            }
            catch (TaskCanceledException)
            {
                Logging.Current.Info(("TaskCanceledException"));
            }
        }

        private async void lmu_extendedReadThread()
        {
            try
            {

                await Task.Delay(500, cts.Token);
                while (!IsEnded)
                {
                    if (!this.lmu_extended_connected)
                    {
                        try
                        {
                            // Extended buffer is the last one constructed, so it is an indicator RF2SM is ready.
                            this.extendedBuffer.Connect();
                            this.rulesBuffer.Connect();
                            
                            this.lmu_extended_connected = true;
                        }
                        catch (Exception)
                        {
                            LMURepairAndRefuelData.Cuts = 0;
                            LMURepairAndRefuelData.CutsMax = 0;
                            LMURepairAndRefuelData.PenaltyLeftLaps = 0;
                            LMURepairAndRefuelData.PenaltyType = 0;
                            LMURepairAndRefuelData.PenaltyCount = 0;
                            LMURepairAndRefuelData.mPendingPenaltyType1 = 0;
                            LMURepairAndRefuelData.mPendingPenaltyType2 = 0;
                            LMURepairAndRefuelData.mPendingPenaltyType3 = 0;
                            LMURepairAndRefuelData.mChangedParamValue = "None";
                            LMURepairAndRefuelData.mChangedParamType = 0;
                            LMURepairAndRefuelData.mpBrakeMigration = 0;
                            LMURepairAndRefuelData.mpBrakeMigrationMax = 0;
                            LMURepairAndRefuelData.mpTractionControl = 0;
                            LMURepairAndRefuelData.mpMotorMap = "None";
                            LMURepairAndRefuelData.VM_ANTILOCKBRAKESYSTEMMAP = "N/A";
                            LMURepairAndRefuelData.VM_BRAKE_BALANCE = "N/A";
                            LMURepairAndRefuelData.VM_BRAKE_MIGRATION = "N/A";
                            LMURepairAndRefuelData.VM_ENGINE_BRAKEMAP = "N/A";
                            LMURepairAndRefuelData.VM_ELECTRIC_MOTOR_MAP = "N/A";
                            LMURepairAndRefuelData.VM_REGEN_LEVEL = "N/A";
                            LMURepairAndRefuelData.VM_TRACTIONCONTROLMAP = "N/A";
                            LMURepairAndRefuelData.VM_TRACTIONCONTROLPOWERCUTMAP = "N/A";
                            LMURepairAndRefuelData.VM_TRACTIONCONTROLSLIPANGLEMAP = "N/A";
                            LMURepairAndRefuelData.VM_FRONT_ANTISWAY = "N/A";
                            LMURepairAndRefuelData.VM_REAR_ANTISWAY = "N/A";
                            this.lmu_extended_connected = false;
                           // Logging.Current.Info("Extended data update service not connectded.");
                        }
                    }
                    else
                    {
                        extendedBuffer.GetMappedData(ref lmu_extended);
                        rulesBuffer.GetMappedData(ref rules);
                        LMURepairAndRefuelData.Cuts = lmu_extended.mCuts;
                        LMURepairAndRefuelData.CutsMax = lmu_extended.mCutsPoints;
                        LMURepairAndRefuelData.PenaltyLeftLaps  = lmu_extended.mPenaltyLeftLaps;
                        LMURepairAndRefuelData.PenaltyType = lmu_extended.mPenaltyType;
                        LMURepairAndRefuelData.PenaltyCount = lmu_extended.mPenaltyCount;
                        LMURepairAndRefuelData.mPendingPenaltyType1 = lmu_extended.mPendingPenaltyType1;
                        LMURepairAndRefuelData.mPendingPenaltyType2 = lmu_extended.mPendingPenaltyType2;
                        LMURepairAndRefuelData.mPendingPenaltyType3 = lmu_extended.mPendingPenaltyType3;
                        LMURepairAndRefuelData.mpBrakeMigration = lmu_extended.mpBrakeMigration;
                        LMURepairAndRefuelData.mpBrakeMigrationMax = lmu_extended.mpBrakeMigrationMax;
                        LMURepairAndRefuelData.mpTractionControl = lmu_extended.mpTractionControl;
                        LMURepairAndRefuelData.mpMotorMap = GetStringFromBytes(lmu_extended.mpMotorMap);
                        string mChangedParamValue = GetStringFromBytes(lmu_extended.mChangedParamValue).Trim();
                        // if data is 0, we act as if we don't received data
                        if (lmu_extended.mChangedParamType == 0 && mChangedParamValue.Equals(""))
                        {
                            LMURepairAndRefuelData.mChangedParamType = -1;
                            LMURepairAndRefuelData.mChangedParamValue = "";
                        }
                        // if we detect a compatible data change in MemoryShared we update the property and flag we need report change
                        else if (lmu_extended.mChangedParamType != -1
                                && (LMURepairAndRefuelData.mChangedParamType != lmu_extended.mChangedParamType ||  //and the value really change since last time
                                    LMURepairAndRefuelData.mChangedParamValue != mChangedParamValue)
                                )
                        {
                            // if data read from MemoryShared is different, we will refresh lmu data
                            NeedUpdateData = true;
                            LMURepairAndRefuelData.mChangedParamType = lmu_extended.mChangedParamType;
                            LMURepairAndRefuelData.mChangedParamValue = mChangedParamValue;

                            switch (LMURepairAndRefuelData.mChangedParamType)
                            {
                                case 3:
                                    LMURepairAndRefuelData.VM_ANTILOCKBRAKESYSTEMMAP = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 10:
                                    LMURepairAndRefuelData.VM_BRAKE_BALANCE = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 15:
                                    LMURepairAndRefuelData.VM_BRAKE_MIGRATION = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 9:
                                    if (LMURepairAndRefuelData.mChangedParamValue.Contains("kW") || LMURepairAndRefuelData.mChangedParamValue.Contains("Off") || LMURepairAndRefuelData.mChangedParamValue.Contains("Safety-car") || LMURepairAndRefuelData.mChangedParamValue.Contains("Race"))
                                    {
                                        if (LMURepairAndRefuelData.CarClass.Contains("Hyper"))
                                        {
                                            LMURepairAndRefuelData.VM_ELECTRIC_MOTOR_MAP = LMURepairAndRefuelData.mChangedParamValue;
                                        }
                                        else
                                        {
                                            LMURepairAndRefuelData.VM_ENGINE_MIXTURE = LMURepairAndRefuelData.mChangedParamValue;
                                        }
                                    }
                                    else
                                    {
                                        if (LMURepairAndRefuelData.CarModel.Equals("Ferrari AF Corse 2024") || LMURepairAndRefuelData.CarModel.Equals("Ferrari AF Corse"))
                                        { LMURepairAndRefuelData.VM_FRONT_ANTISWAY = frontABR["F" + LMURepairAndRefuelData.mChangedParamValue]; }
                                        else if (LMURepairAndRefuelData.CarModel.Equals("Peugeot TotalEnergies 2024") || LMURepairAndRefuelData.CarModel.Equals("Porsche Penske Motorsport 2024") || LMURepairAndRefuelData.CarModel.Equals("Toyota Gazoo Racing 2024") || LMURepairAndRefuelData.CarModel.Equals("Peugeot TotalEnergies") || LMURepairAndRefuelData.CarModel.Equals("Porsche Penske Motorsport") || LMURepairAndRefuelData.CarModel.Equals("Toyota Gazoo Racing"))
                                        { LMURepairAndRefuelData.VM_FRONT_ANTISWAY = frontABR["P" + LMURepairAndRefuelData.mChangedParamValue]; }
                                        else if (LMURepairAndRefuelData.CarModel.Equals("Glickenhaus Racing"))
                                        { LMURepairAndRefuelData.VM_FRONT_ANTISWAY = frontABR["G" + LMURepairAndRefuelData.mChangedParamValue]; }
                                        else
                                        { LMURepairAndRefuelData.VM_FRONT_ANTISWAY = frontABR[LMURepairAndRefuelData.mChangedParamValue]; }
                                    }
                                    break;
                                case 11:
                                    LMURepairAndRefuelData.VM_REGEN_LEVEL = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 7:
                                    LMURepairAndRefuelData.VM_TRACTIONCONTROLSLIPANGLEMAP = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 6:
                                    LMURepairAndRefuelData.VM_TRACTIONCONTROLPOWERCUTMAP = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 2:
                                    LMURepairAndRefuelData.VM_TRACTIONCONTROLMAP = LMURepairAndRefuelData.mChangedParamValue;
                                    break;
                                case 8:
                                    if (LMURepairAndRefuelData.CarModel.Equals("Ferrari AF Corse 2024") || LMURepairAndRefuelData.CarModel.Equals("Ferrari AF Corse"))
                                    { LMURepairAndRefuelData.VM_REAR_ANTISWAY = rearABR["F" + LMURepairAndRefuelData.mChangedParamValue]; }
                                    else if (LMURepairAndRefuelData.CarModel.Equals("Peugeot TotalEnergies 2024") || LMURepairAndRefuelData.CarModel.Equals("Porsche Penske Motorsport 2024") || LMURepairAndRefuelData.CarModel.Equals("Toyota Gazoo Racing 2024") || LMURepairAndRefuelData.CarModel.Equals("Peugeot TotalEnergies") || LMURepairAndRefuelData.CarModel.Equals("Porsche Penske Motorsport") || LMURepairAndRefuelData.CarModel.Equals("Toyota Gazoo Racing"))
                                    { LMURepairAndRefuelData.VM_REAR_ANTISWAY = rearABR["P" + LMURepairAndRefuelData.mChangedParamValue]; }
                                    else if (LMURepairAndRefuelData.CarModel.Equals("Glickenhaus Racing"))
                                    { LMURepairAndRefuelData.VM_REAR_ANTISWAY = rearABR["G" + LMURepairAndRefuelData.mChangedParamValue]; }
                                    else
                                    { LMURepairAndRefuelData.VM_REAR_ANTISWAY = rearABR[LMURepairAndRefuelData.mChangedParamValue]; }

                                    break;
                                default:
                                    // code block
                                    break;
                            }
                        }

                            // Logging.Current.Info(("Extended data update service connectded. " +  lmu_extended.mCutsPoints.ToString() + " Penalty laps" + lmu_extended.mPenaltyLeftLaps).ToString());
                    }
                    // if we are connected we wait a short time before read again the memory
                    if (this.lmu_extended_connected) {
                        await Task.Delay(ButtonBindSettings.GetMemoryDataThreadTimeout, cts.Token);
                    } 
                    // if we are not connected we wait 5 secondes before attempted a new connection 
                    else
                    {
                        await Task.Delay(5000, cts.Token);
                    }
                }
            }
            catch (AggregateException)
            {
                Logging.Current.Info(("AggregateException"));
            }
            catch (TaskCanceledException)
            {
                Logging.Current.Info(("TaskCanceledException"));
            }
        }
        private async Task<string> FetchTireManagementJSONdata()
        {
            try
            {
                var urlTireManagement = "http://localhost:6397/rest/garage/UIScreen/TireManagement";
                var responseTireManagement = await _httpClient.GetStringAsync(urlTireManagement);
                return responseTireManagement;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch TireManagement data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private async Task<string> FetchRepairAndRefuelJSONdata()
        {
            try
            {
                var urlRepairAndRefuel = "http://localhost:6397/rest/garage/UIScreen/RepairAndRefuel";
                var responseRepairAndRefuel = await _httpClient.GetStringAsync(urlRepairAndRefuel);
                return responseRepairAndRefuel;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch RepairAndRefuel data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private async Task<string> FetchGetGameStateJSONdata()
        {
            try
            {
                var urlGetGameState = "http://localhost:6397/rest/sessions/GetGameState";
                var responseGetGameState = await _httpClient.GetStringAsync(urlGetGameState);
                return responseGetGameState;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch GetGameState data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private async Task<string> FetchInfoForEventJSONdata()
        {
            try
            {
                var urlInfoForEvent = "http://localhost:6397/rest/sessions/GetSessionsInfoForEvent";
                var responseInfoForEvent = await _httpClient.GetStringAsync(urlInfoForEvent);
                return responseInfoForEvent;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch InfoForEvent data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private async Task<string> FetchRaceHistoryJSONdata()
        {
            try
            {
                var urlRaceHistory = "http://localhost:6397/rest/garage/UIScreen/RaceHistory";
                var responseRaceHistory = await _httpClient.GetStringAsync(urlRaceHistory);
                return responseRaceHistory;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch RaceHistory data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private async Task<string> FetchPitMenuJSONdata()
        {
            try
            {
                var urlPitMenu = "http://localhost:6397/rest/garage/PitMenu/receivePitMenu";
                var responsePitMenu = await _httpClient.GetStringAsync(urlPitMenu);
                return responsePitMenu;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch PitMenu data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private async Task<string> FetchCarSetupOverviewJSONdata()
        {
            try
            {
                var urlCarSetupOverview = "http://localhost:6397/rest/garage/UIScreen/CarSetupOverview";
                var responseCarSetupOverview = await _httpClient.GetStringAsync(urlCarSetupOverview);
                return responseCarSetupOverview;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"Failed to fetch CarSetupOverview data: {ex.Message}");
                return string.Empty; // Return an empty string in case of an error
            }
        }
        private float GetPMCValue(JArray pitMenuJSONData, int pmcValue)
        {
            JToken item = pitMenuJSONData?.FirstOrDefault(x => (int?)x["PMC Value"] == pmcValue);

            if (item != null && item["currentSetting"] != null)
            {
                float currentSetting = (float)item["currentSetting"];

                return currentSetting;
            }
            return 0;
        }
        private string GetPMCText(JArray pitMenuJSONData, int pmcValue, string defaultValue = "Unknown")
        {
            JToken item = pitMenuJSONData?.FirstOrDefault(x => (int?)x["PMC Value"] == pmcValue);

            if (item != null && item["currentSetting"] != null)
            {
                int currentSetting = (int)item["currentSetting"];
                JToken setting = item["settings"]?[currentSetting];

                return setting?["text"]?.ToString() ?? defaultValue;
            }
            return defaultValue;
        }
        private void SetTyrePressureData(JArray pitMenuJSONData, int pmcValue, out int pressure_kPa, out float pressure_Bar, out float pressure_Psi)
        {
            int rawPressure = (int)GetPMCValue(pitMenuJSONData, pmcValue);
            {
                pressure_kPa = rawPressure + 135;
                pressure_Bar = (float)Math.Round(pressure_kPa / 100.0, 2);
                pressure_Psi = (float)Math.Round(pressure_kPa * PsiConvert, 2);
            }
        }

        private static string GetStringFromBytes(byte[] bytes)
        {
            if (bytes == null)
                return "null";

            var nullIdx = Array.IndexOf(bytes, (byte)0);

            return nullIdx >= 0
              ? Encoding.Default.GetString(bytes, 0, nullIdx)
              : Encoding.Default.GetString(bytes);
        }
        //public static rF2VehicleScoring GetPlayerScoring(ref rF2Scoring scoring)
        //{
        //    var playerVehScoring = new rF2VehicleScoring();
        //    for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; ++i)
        //    {
        //        var vehicle = scoring.mVehicles[i];
        //        switch ((LMU_Constants.rF2Control)vehicle.mControl)
        //        {
        //            case LMU_Constants.rF2Control.AI:
        //            case LMU_Constants.rF2Control.Player:
        //            case LMU_Constants.rF2Control.Remote:
        //                if (vehicle.mIsPlayer == 1)
        //                    playerVehScoring = vehicle;

        //                break;

        //            default:
        //                continue;
        //        }

        //        if (playerVehScoring.mIsPlayer == 1)
        //            break;
        //    }

        //    return playerVehScoring;
        //}
        //public static List<rF2VehicleScoring> GetOpenentsScoring(ref rF2Scoring scoring)
        //{
        //    List<rF2VehicleScoring> playersVehScoring  = new List<rF2VehicleScoring>();
        //    for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; ++i)
        //    {
        //        var vehicle = scoring.mVehicles[i];
        //        switch ((LMU_Constants.rF2Control)vehicle.mControl)
        //        {
        //            case LMU_Constants.rF2Control.AI:
        //                //if (vehicle.mIsPlayer != 1)
        //                    playersVehScoring.Add(vehicle);
        //                break;
        //            case LMU_Constants.rF2Control.Player:
        //            case LMU_Constants.rF2Control.Remote:
        //                //if (vehicle.mIsPlayer != 1)
        //                    playersVehScoring.Add(vehicle);

        //                break;

        //            default:
        //                continue;
        //        }
        //     }
        //    return playersVehScoring;
        //}

        private void addPropertyToSimHUB(PluginManager pluginManager) {
            //pluginManager.AddProperty("Redadeg.lmu.CurrentLapTimeDifOldNew", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Batt.Current", this.GetType(), LMURepairAndRefuelData.currentBattery);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Batt.Current_%", this.GetType(), LMURepairAndRefuelData.currentBatteryP);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Batt.Max", this.GetType(), LMURepairAndRefuelData.maxBattery);
            pluginManager.AddProperty("Redadeg.lmu.Energy.ComputedFuelRatio_LastLap", this.GetType(), LMURepairAndRefuelData.ComputedFuelRatio_LastLap);
            pluginManager.AddProperty("Redadeg.lmu.Energy.ComputedFuelRatio_PerLast5Lap", this.GetType(), LMURepairAndRefuelData.ComputedFuelRatio_Last5Lap);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Fuel.Current_L", this.GetType(), LMURepairAndRefuelData.currentFuel);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Fuel.FractionPerLap_%", this.GetType(), LMURepairAndRefuelData.fuelFractionPerLap);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Fuel.Max_L", this.GetType(), LMURepairAndRefuelData.maxFuel);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Fuel.NeededUntilEnd_L", this.GetType(), LMURepairAndRefuelData.FuelNeededUntilEnd);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Fuel.toRefill_L", this.GetType(), LMURepairAndRefuelData.FueltoRefill);
            pluginManager.AddProperty("Redadeg.lmu.Energy.Fuel.toRefill_safe_L", this.GetType(), LMURepairAndRefuelData.FueltoRefill_safe);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.Current_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergy);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.Current", this.GetType(), LMURepairAndRefuelData.currentVirtualEnergy);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.FractionPerLap_%", this.GetType(), LMURepairAndRefuelData.virtualEnergyFractionPerLap);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.Max", this.GetType(), LMURepairAndRefuelData.maxVirtualEnergy);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.NeededUntilEnd_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergyNeededUntilEnd);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.PerLast5ClearLap_%", this.GetType(), LMURepairAndRefuelData.energyPerLast5ClearLap);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.PerLast5Lap_%", this.GetType(), LMURepairAndRefuelData.energyPerLast5Lap);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.PerLastLap_%", this.GetType(), LMURepairAndRefuelData.energyPerLastLap);
            //pluginManager.AddProperty("Redadeg.lmu.Energy.VE.PerLastLap_RealTime", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.TimeElapsed", this.GetType(), LMURepairAndRefuelData.energyTimeElapsed);
            //pluginManager.AddProperty("Redadeg.lmu.Energy.VE.TimeElapsed_RealTime", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.toRefill_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergytoRefill);
            pluginManager.AddProperty("Redadeg.lmu.Energy.VE.toRefill_safe_%", this.GetType(), LMURepairAndRefuelData.VirtualEnergytoRefill_safe);
            
            pluginManager.AddProperty("Redadeg.lmu.Extended.BrakeMigration", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.BrakeMigrationMax", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.ChangedParamType", this.GetType(), -1);
            pluginManager.AddProperty("Redadeg.lmu.Extended.ChangedParamValue", this.GetType(), "None");
            pluginManager.AddProperty("Redadeg.lmu.Extended.Cuts", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.CutsMax", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.MotorMap", this.GetType(), "None");
            pluginManager.AddProperty("Redadeg.lmu.Extended.PenaltyCount", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.PenaltyLeftLaps", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.PenaltyType", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.PendingPenaltyType1", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.PendingPenaltyType2", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.PendingPenaltyType3", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.TractionControl", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_ANTILOCKBRAKESYSTEMMAP", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_BRAKE_BALANCE", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_BRAKE_MIGRATION", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_ELECTRIC_MOTOR_MAP", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_ENGINE_BRAKEMAP", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_ENGINE_MIXTURE", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_FRONT_ANTISWAY", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_REAR_ANTISWAY", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_REGEN_LEVEL", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_TRACTIONCONTROLMAP", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_TRACTIONCONTROLPOWERCUTMAP", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.Extended.VM_TRACTIONCONTROLSLIPANGLEMAP", this.GetType(), "");

            pluginManager.AddProperty("Redadeg.lmu.GameInfos.isReplayActive", this.GetType(), LMURepairAndRefuelData.isReplayActive);
            pluginManager.AddProperty("Redadeg.lmu.GameInfos.MultiStintState", this.GetType(), LMURepairAndRefuelData.MultiStintState);
            pluginManager.AddProperty("Redadeg.lmu.GameInfos.PitEntryDist", this.GetType(), LMURepairAndRefuelData.PitEntryDist);
            pluginManager.AddProperty("Redadeg.lmu.GameInfos.PitState", this.GetType(), LMURepairAndRefuelData.PitState);
            pluginManager.AddProperty("Redadeg.lmu.GameInfos.RaceFinished", this.GetType(), LMURepairAndRefuelData.raceFinished);
            pluginManager.AddProperty("Redadeg.lmu.GameInfos.timeOfDay", this.GetType(), LMURepairAndRefuelData.timeOfDay);
            pluginManager.AddProperty("Redadeg.lmu.GameInfos.lengthTime", this.GetType(), LMURepairAndRefuelData.lengthTime);

            pluginManager.AddProperty("Redadeg.lmu.mMessage", this.GetType(), "");
            pluginManager.AddProperty("Redadeg.lmu.NewLap", this.GetType(), 0);
            pluginManager.AddProperty("Redadeg.lmu.passStopAndGo", this.GetType(), LMURepairAndRefuelData.passStopAndGo);

            pluginManager.AddProperty("Redadeg.lmu.Pit.MaxAvailableTires", this.GetType(), LMURepairAndRefuelData.maxAvailableTires);
            pluginManager.AddProperty("Redadeg.lmu.Pit.NewTires", this.GetType(), LMURepairAndRefuelData.newTires);
            pluginManager.AddProperty("Redadeg.lmu.Pit.StopLength", this.GetType(), LMURepairAndRefuelData.pitStopLength);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.FuelRatio", this.GetType(), LMURepairAndRefuelData.FuelRatio);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Grille", this.GetType(), LMURepairAndRefuelData.Grille);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.RepairDamage", this.GetType(), LMURepairAndRefuelData.RepairDamage);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.ReplaceBrakes", this.GetType(), LMURepairAndRefuelData.replaceBrakes);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_kPa);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_kPa_Text);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fl_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.fl_Tyre_NewPressure_Psi);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fl_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.fl_TyreChange_Name);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_kPa);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_kPa_Text);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fr_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.fr_Tyre_NewPressure_Psi);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.fr_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.fr_TyreChange_Name);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_kPa);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_kPa_Text);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rl_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.rl_Tyre_NewPressure_Psi);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rl_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.rl_TyreChange_Name);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_Bar", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_kPa", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_kPa);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_kPa_Text", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_kPa_Text);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rr_Tyre_NewPressure_Psi", this.GetType(), LMURepairAndRefuelData.rr_Tyre_NewPressure_Psi);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Tyre.rr_TyreChange_Name", this.GetType(), LMURepairAndRefuelData.rr_TyreChange_Name);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Virtual_Energy", this.GetType(), LMURepairAndRefuelData.PitMVirtualEnergy);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Virtual_Energy_Text", this.GetType(), LMURepairAndRefuelData.PitMVirtualEnergy_Text);
            pluginManager.AddProperty("Redadeg.lmu.PitMenu.Wing", this.GetType(), LMURepairAndRefuelData.Wing);

            pluginManager.AddProperty("Redadeg.lmu.selectedMenuIndex", this.GetType(), 0);

            pluginManager.AddProperty("Redadeg.lmu.TeamInfos.Driver", this.GetType(), LMURepairAndRefuelData.Driver);
            pluginManager.AddProperty("Redadeg.lmu.TeamInfos.TeamName", this.GetType(), LMURepairAndRefuelData.teamName);
            pluginManager.AddProperty("Redadeg.lmu.TeamInfos.VehicleName", this.GetType(), LMURepairAndRefuelData.vehicleName);

            pluginManager.AddProperty("Redadeg.lmu.TrackInfos.GrandPrixName", this.GetType(), LMURepairAndRefuelData.grandPrixName);
            pluginManager.AddProperty("Redadeg.lmu.TrackInfos.Location", this.GetType(), LMURepairAndRefuelData.location);
            pluginManager.AddProperty("Redadeg.lmu.TrackInfos.OpeningYear", this.GetType(), LMURepairAndRefuelData.openingYear);
            pluginManager.AddProperty("Redadeg.lmu.TrackInfos.TrackLength", this.GetType(), LMURepairAndRefuelData.trackLength);
            pluginManager.AddProperty("Redadeg.lmu.TrackInfos.TrackName", this.GetType(), LMURepairAndRefuelData.trackName);

            pluginManager.AddProperty("Redadeg.lmu.Tyre.fl_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.fl_TyreCompound_Name);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.fl_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.fl_TyrePressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.fl_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.fl_TyrePressure);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.fl_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.fl_TyrePressure_Psi);

            pluginManager.AddProperty("Redadeg.lmu.Tyre.fr_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.fr_TyreCompound_Name);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.fr_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.fr_TyrePressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.fr_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.fr_TyrePressure);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.fr_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.fr_TyrePressure_Psi);

            pluginManager.AddProperty("Redadeg.lmu.Tyre.rl_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.rl_TyreCompound_Name);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.rl_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.rl_TyrePressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.rl_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.rl_TyrePressure);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.rl_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.rl_TyrePressure_Psi);

            pluginManager.AddProperty("Redadeg.lmu.Tyre.rr_TyreCompound_Name", this.GetType(), LMURepairAndRefuelData.rr_TyreCompound_Name);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.rr_TyrePressure_Bar", this.GetType(), LMURepairAndRefuelData.rr_TyrePressure_Bar);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.rr_TyrePressure_kPa", this.GetType(), LMURepairAndRefuelData.rr_TyrePressure);
            pluginManager.AddProperty("Redadeg.lmu.Tyre.rr_TyrePressure_Psi", this.GetType(), LMURepairAndRefuelData.rr_TyrePressure_Psi);

            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Current.AmbientTemp", this.GetType(), LMURepairAndRefuelData.ambientTemp);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Current.CloudCoverage_%", this.GetType(), LMURepairAndRefuelData.cloudCoverage);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Current.Humidity_%", this.GetType(), LMURepairAndRefuelData.humidity);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Current.LightLevel_%", this.GetType(), LMURepairAndRefuelData.lightLevel);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Current.Raining_%", this.GetType(), LMURepairAndRefuelData.raining);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Current.RainIntensity_%", this.GetType(), LMURepairAndRefuelData.rainIntensity);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.RainChance_%", this.GetType(), LMURepairAndRefuelData.rainChance);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Track.Temp", this.GetType(), LMURepairAndRefuelData.trackTemp);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Track.Wetness_%", this.GetType(), LMURepairAndRefuelData.trackWetness);
            pluginManager.AddProperty("Redadeg.lmu.WeatherInfos.Track.Wetness_Text", this.GetType(), LMURepairAndRefuelData.trackWetness_Text);
        }

        private void initFrontABRDict() {
            frontABR = new Dictionary<string, string>();
            try
            {
                //Add front ABR
                frontABR.Add("Détaché", "D�tach�");
                frontABR.Add("Detached", "Detached");
                frontABR.Add("866 N/mm", "P1");
                frontABR.Add("1069 N/mm", "P2");
                frontABR.Add("1271 N/mm", "P3");
                frontABR.Add("1473 N/mm", "P4");
                frontABR.Add("1676 N/mm", "P5");

                //ferrary
                frontABR.Add("FDétaché", "D�tach�");
                frontABR.Add("FDetached", "Detached");
                frontABR.Add("F94 N/mm", "A-P1");
                frontABR.Add("F107 N/mm", "A-P2");
                frontABR.Add("F133 N/mm", "A-P3");
                frontABR.Add("F172 N/mm", "A-P4");
                frontABR.Add("F254 N/mm", "A-P5");

                frontABR.Add("F232 N/mm", "B-P1");
                frontABR.Add("F262 N/mm", "B-P2");
                frontABR.Add("F307 N/mm", "B-P3");
                frontABR.Add("F364 N/mm", "B-P4");
                frontABR.Add("F440 N/mm", "B-P5");

                frontABR.Add("F312 N/mm", "C-P1");
                frontABR.Add("F332 N/mm", "C-P2");
                frontABR.Add("F365 N/mm", "C-P3");
                frontABR.Add("F403 N/mm", "C-P4");
                frontABR.Add("F450 N/mm", "C-P5");

                frontABR.Add("F426 N/mm", "D-P1");
                frontABR.Add("F469 N/mm", "D-P2");
                frontABR.Add("F530 N/mm", "D-P3");
                frontABR.Add("F599 N/mm", "D-P4");
                frontABR.Add("F685 N/mm", "D-P5");

                frontABR.Add("F632 N/mm", "E-P1");
                frontABR.Add("F748 N/mm", "E-P2");
                frontABR.Add("F929 N/mm", "E-P3");
                frontABR.Add("F1152 N/mm", "E-P4");
                frontABR.Add("F1473 N/mm", "E-P5");


                //pegeout
                frontABR.Add("PDétaché", "D�tach�");
                frontABR.Add("PDetached", "Detached");
                frontABR.Add("P428 N/mm", "P1");
                frontABR.Add("P487 N/mm", "P2");
                frontABR.Add("P559 N/mm", "P3");
                frontABR.Add("P819 N/mm", "P4");
                frontABR.Add("P932 N/mm", "P5");

                frontABR.Add("P1069 N/mm", "P6");
                frontABR.Add("P1545 N/mm", "P7");
                frontABR.Add("P1758 N/mm", "P8");
                frontABR.Add("P2018 N/mm", "P9");
                frontABR.Add("P2689 N/mm", "P10");

                frontABR.Add("P3059 N/mm", "P11");
                frontABR.Add("P3512 N/mm", "P12");
                frontABR.Add("P3889 N/mm", "P13");
                frontABR.Add("P4425 N/mm", "P14");
                frontABR.Add("P5080 N/mm", "P15");

                //Glickenhaus Racing
                frontABR.Add("GDétaché", "D�tach�");
                frontABR.Add("GDetached", "Detached");
                frontABR.Add("G86 N/mm", "P1");
                frontABR.Add("G97 N/mm", "P2");
                frontABR.Add("G112 N/mm", "P3");
                frontABR.Add("G164 N/mm", "P4");
                frontABR.Add("G186 N/mm", "P5");

                frontABR.Add("G214 N/mm", "P6");
                frontABR.Add("G309 N/mm", "P7");
                frontABR.Add("G352 N/mm", "P8");
                frontABR.Add("G404 N/mm", "P9");
                frontABR.Add("G538 N/mm", "P10");

                frontABR.Add("G612 N/mm", "P11");
                frontABR.Add("G702 N/mm", "P12");
                frontABR.Add("G778 N/mm", "P13");
                frontABR.Add("G885 N/mm", "P14");
                frontABR.Add("G1016 N/mm", "P15");
            }
            catch { }
        }

        private void initBackABRDict() {
            rearABR = new Dictionary<string, string>();
            try
            {
                //add rear abr
                rearABR.Add("Détaché", "D�tach�");
                rearABR.Add("Detached", "Detached");
                rearABR.Add("492 N/mm", "P1");
                rearABR.Add("638 N/mm", "P2");
                rearABR.Add("784 N/mm", "P3");
                rearABR.Add("930 N/mm", "P4");
                rearABR.Add("1077 N/mm", "P5");
                //ferrary
                rearABR.Add("FDétaché", "D�tach�");
                rearABR.Add("FDetached", "Detached");
                rearABR.Add("F98 N/mm", "A-P1");
                rearABR.Add("F120 N/mm", "A-P2");
                rearABR.Add("F142 N/mm", "A-P3");
                rearABR.Add("F166 N/mm", "A-P4");
                rearABR.Add("F184 N/mm", "A-P5");

                rearABR.Add("F171 N/mm", "B-P1");
                rearABR.Add("F211 N/mm", "B-P2");
                rearABR.Add("F253 N/mm", "B-P3");
                rearABR.Add("F299 N/mm", "B-P4");
                rearABR.Add("F344 N/mm", "B-P5");

                rearABR.Add("F275 N/mm", "C-P1");
                rearABR.Add("F306 N/mm", "C-P2");
                rearABR.Add("F330 N/mm", "C-P3");
                rearABR.Add("F355 N/mm", "C-P4");
                rearABR.Add("F368 N/mm", "C-P5");

                rearABR.Add("F317 N/mm", "D-P1");
                rearABR.Add("F357 N/mm", "D-P2");
                rearABR.Add("F393 N/mm", "D-P3");
                rearABR.Add("F428 N/mm", "D-P4");
                rearABR.Add("F452 N/mm", "D-P5");

                rearABR.Add("F435 N/mm", "E-P1");
                rearABR.Add("F514 N/mm", "E-P2");
                rearABR.Add("F590 N/mm", "E-P3");
                rearABR.Add("F668 N/mm", "E-P4");
                rearABR.Add("F736 N/mm", "E-P5");

                //pegeout
                rearABR.Add("PDétaché", "D�tach�");
                rearABR.Add("PDetached", "Detached");
                rearABR.Add("P119 N/mm", "P1");
                rearABR.Add("P144 N/mm", "P2");
                rearABR.Add("P178 N/mm", "P3");
                rearABR.Add("P206 N/mm", "P4");
                rearABR.Add("P250 N/mm", "P5");

                rearABR.Add("P308 N/mm", "P6");
                rearABR.Add("P393 N/mm", "P7");
                rearABR.Add("P476 N/mm", "P8");
                rearABR.Add("P587 N/mm", "P9");
                rearABR.Add("P732 N/mm", "P10");

                rearABR.Add("P886 N/mm", "P11");
                rearABR.Add("P1094 N/mm", "P12");
                rearABR.Add("P1330 N/mm", "P13");
                rearABR.Add("P1610 N/mm", "P14");
                rearABR.Add("P1987 N/mm", "P15");

                //Glickenhaus Racing
                rearABR.Add("GDétaché", "D�tach�");
                rearABR.Add("GDetached", "Detached");
                rearABR.Add("G48 N/mm", "P1");
                rearABR.Add("G58 N/mm", "P2");
                rearABR.Add("G71 N/mm", "P3");
                rearABR.Add("G82 N/mm", "P4");
                rearABR.Add("G100 N/mm", "P5");

                rearABR.Add("G123 N/mm", "P6");
                rearABR.Add("G157 N/mm", "P7");
                rearABR.Add("G190 N/mm", "P8");
                rearABR.Add("G235 N/mm", "P9");
                rearABR.Add("G293 N/mm", "P10");

                rearABR.Add("G354 N/mm", "P11");
                rearABR.Add("G437 N/mm", "P12");
                rearABR.Add("G532 N/mm", "P13");
                rearABR.Add("G644 N/mm", "P14");
                rearABR.Add("G795 N/mm", "P15");
            }
            catch { }
        }
    }

    //public class for exchanging the data with the main cs file (Init and DataUpdate function)
    public class LMURepairAndRefuelData
    {

        public static float mPlayerBestLapTime { get; set; }
        public static float mPlayerBestLapSector1 { get; set; }
        public static float mPlayerBestLapSector2 { get; set; }
        public static float mPlayerBestLapSector3 { get; set; }
        public static float mPlayerBestSector1 { get; set; }
        public static float mPlayerBestSector2 { get; set; }
        public static float mPlayerBestSector3 { get; set; }
        public static float mPlayerCurSector1 { get; set; }
        public static float mPlayerCurSector2 { get; set; }
        public static float mPlayerCurSector3 { get; set; }
        public static float mSessionBestSector1 { get; set; }
        public static float mSessionBestSector2 { get; set; }
        public static float mSessionBestSector3 { get; set; }
        public static double sessionTimeLeftInSeconds { get; internal set; }

        //public static double allTimeBestInSeconds { get; internal set; }
        public static double BestLapTime { get; internal set; }
        public static int mpBrakeMigration { get; set; }
        public static int mpBrakeMigrationMax { get; set; }
        public static int mpTractionControl { get; set; }
        public static string mpMotorMap { get; set; }
        public static int mChangedParamType { get; set; }
        public static string mChangedParamValue { get; set; }
        public static float Cuts { get; set; }
        public static int CutsMax { get; set; }
        public static int PenaltyLeftLaps { get; set; }
        public static int PenaltyType { get; set; }
        public static int PenaltyCount { get; set; }
        public static int mPendingPenaltyType1 { get; set; }
        public static int mPendingPenaltyType2 { get; set; }
        public static int mPendingPenaltyType3 { get; set; }
        public static float energyTimeElapsed { get; set; }
        public static float energyPerLastLap { get; set; }
        public static float energyPerLast5Lap { get; set; }
        public static float energyPerLast5ClearLap { get; set; }
        public static float currentFuel { get; set; }
        public static int currentVirtualEnergy { get; set; }
        public static int currentBattery { get; set; }
        public static int currentBatteryP { get; set; }
        public static int maxBattery { get; set; }
        public static int maxFuel { get; set; }
        public static int maxVirtualEnergy { get; set; }
        public static string RepairDamage { get; set; }
        public static string passStopAndGo { get; set; }
        public static string Driver { get; set; }
        public static float VirtualEnergy { get; set; }
        public static string addVirtualEnergy { get; set; }
        public static string addFuel { get; set; }
        public static string Wing { get; set; }
        public static string Grille { get; set; }
        public static int maxAvailableTires { get; set; }
        public static int newTires { get; set; }
        public static string replaceBrakes { get; set; }
        public static float FuelRatio { get; set; }
        public static float pitStopLength { get; set; }
        public static string path { get; set; }
        public static string timeOfDay { get; set; }
        public static int rainChance { get; set; }
        public static string VM_ANTILOCKBRAKESYSTEMMAP { get; set; }
        public static string VM_BRAKE_BALANCE { get; set; }
        public static string VM_BRAKE_MIGRATION { get; set; }
        public static string VM_ENGINE_BRAKEMAP { get; set; }
        public static string VM_ELECTRIC_MOTOR_MAP { get; set; }
        public static string VM_ENGINE_MIXTURE { get; set; }
        public static string VM_REGEN_LEVEL { get; set; }
        public static string VM_TRACTIONCONTROLMAP { get; set; }
        public static string VM_TRACTIONCONTROLPOWERCUTMAP { get; set; }
        public static string VM_TRACTIONCONTROLSLIPANGLEMAP { get; set; }
        public static string VM_REAR_ANTISWAY { get; set; }
        public static string VM_FRONT_ANTISWAY { get; set; }
        public static string CarClass { get; set; }
        public static string CarModel { get; set; }
        public static string SessionTypeName { get; set; }
        public static int IsInPit { get; set; }
        public static string fl_TyreChange { get; set; }
        public static string fr_TyreChange { get; set; }
        public static string rl_TyreChange { get; set; }
        public static string rr_TyreChange { get; set; }
        public static float fl_TyrePressure { get; set; }
        public static float fr_TyrePressure { get; set; }
        public static float rl_TyrePressure { get; set; }
        public static float rr_TyrePressure { get; set; }
        public static string fl_TyreChange_Name { get; set; }
        public static string fr_TyreChange_Name { get; set; }
        public static string rl_TyreChange_Name { get; set; }
        public static string rr_TyreChange_Name { get; set; }
        public static float fl_TyrePressure_Bar { get; set; }
        public static float fr_TyrePressure_Bar { get; set; }
        public static float rl_TyrePressure_Bar { get; set; }
        public static float rr_TyrePressure_Bar { get; set; }
        public static float fl_TyrePressure_Psi { get; set; }
        public static float fr_TyrePressure_Psi { get; set; }
        public static float rl_TyrePressure_Psi { get; set; }
        public static float rr_TyrePressure_Psi { get; set; }
        public static string fl_TyreCompound_Name { get; set; }
        public static string fr_TyreCompound_Name { get; set; }
        public static string rl_TyreCompound_Name { get; set; }
        public static string rr_TyreCompound_Name { get; set; }
        public static string grandPrixName { get; set; }
        public static string location { get; set; }
        public static string openingYear { get; set; }
        public static string trackLength { get; set; }
        public static string trackName { get; set; }
        public static string teamName { get; set; }
        public static string vehicleName { get; set; }
        public static string raceFinished { get; set; }
        public static string isReplayActive { get; set; }
        public static string PitState { get; set; }
        public static float PitEntryDist { get; set; }
        public static string MultiStintState { get; set; }
        public static float fuelConsumption { get; set; }
        public static float fuelFractionPerLap { get; set; }
        public static float virtualEnergyFractionPerLap { get; set; }
        public static float trackTemp { get; set; }
        public static float trackWetness { get; set; }
        public static float fl_Tyre_NewPressure_kPa { get; set; }
        public static float fr_Tyre_NewPressure_kPa { get; set; }
        public static float rl_Tyre_NewPressure_kPa { get; set; }
        public static float rr_Tyre_NewPressure_kPa { get; set; }
        public static float fl_Tyre_NewPressure_Bar { get; set; }
        public static float fr_Tyre_NewPressure_Bar { get; set; }
        public static float rl_Tyre_NewPressure_Bar { get; set; }
        public static float rr_Tyre_NewPressure_Bar { get; set; }
        public static float fl_Tyre_NewPressure_Psi { get; set; }
        public static float fr_Tyre_NewPressure_Psi { get; set; }
        public static float rl_Tyre_NewPressure_Psi { get; set; }
        public static float rr_Tyre_NewPressure_Psi { get; set; }
        public static string rr_Tyre_NewPressure_kPa_Text { get; set; }
        public static string rl_Tyre_NewPressure_kPa_Text { get; set; }
        public static string fr_Tyre_NewPressure_kPa_Text { get; set; }
        public static string fl_Tyre_NewPressure_kPa_Text { get; set; }
        public static float PitMVirtualEnergy { get; set; }
        public static string PitMVirtualEnergy_Text { get; set; }
        public static string trackWetness_Text { get; set; }
        public static float ambientTemp { get; set; }
        public static float cloudCoverage { get; set; }
        public static float humidity { get; set; }
        public static float lightLevel { get; set; }
        public static float rainIntensity { get; set; }
        public static float raining { get; set; }
        public static float FuelNeededUntilEnd { get; set; }
        public static float VirtualEnergyNeededUntilEnd { get; set; }
        public static float ComputedFuelRatio_LastLap { get; set; }
        public static float ComputedFuelRatio_Last5Lap { get; set; }
        public static float FueltoRefill { get; set; }
        public static float VirtualEnergytoRefill { get; set; }
        public static float FueltoRefill_safe { get; set; }
        public static float VirtualEnergytoRefill_safe { get; set; }
        public static float sh_fuelConsumptionValue { get; internal set; }
        public static int lengthTime { get; internal set; }
    }
}
