using System;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq; // Needed for JObject 
using System.IO;    // Needed for read/write JSON settings file
using SimHub;   // Needed for Logging
using System.Windows.Markup;
using System.Threading.Tasks;


namespace Redadeg.lmuDataPlugin
{
    /// <summary>
    /// Logique d'interaction pour SettingsControlDemo.xaml
    /// </summary>

    public partial class SettingsControl : UserControl, IComponentConnector
    {
        internal Delegate _CreateDelegate(Type delegateType, string handler)
        {
            return Delegate.CreateDelegate(delegateType, this, handler);
        }

        public SettingsControl()
        {
            InitializeComponent();


        }

        void OnLoad(object sender, RoutedEventArgs e)
        {
            try
            {
                JObject JSONSettingsdata = JObject.Parse(File.ReadAllText(LMURepairAndRefuelData.path));
                ButtonBindSettings.Clock_Format24 = JSONSettingsdata["Clock_Format24"] != null ? (bool)JSONSettingsdata["Clock_Format24"] : false;
                ButtonBindSettings.RealTimeClock = JSONSettingsdata["RealTimeClock"] != null ? (bool)JSONSettingsdata["RealTimeClock"] : false;
                ButtonBindSettings.GetMemoryDataThreadTimeout = JSONSettingsdata["GetMemoryDataThreadTimeout"] != null ? (int)JSONSettingsdata["GetMemoryDataThreadTimeout"] : 50;
                ButtonBindSettings.DataUpdateThreadTimeout = JSONSettingsdata["DataUpdateThreadTimeout"] != null ? (int)JSONSettingsdata["DataUpdateThreadTimeout"] : 400;
                ButtonBindSettings.ConsUpdateThreadTimeout = JSONSettingsdata["ConsUpdateThreadTimeout"] != null ? (int)JSONSettingsdata["ConsUpdateThreadTimeout"] : 100;
            }
            catch { }
            clock_format24.IsChecked = ButtonBindSettings.Clock_Format24;
            RealTimeClock.IsChecked = ButtonBindSettings.RealTimeClock;
            GetMemoryDataThreadTimeout.Value = ButtonBindSettings.GetMemoryDataThreadTimeout;
            DataUpdateThreadTimeout.Value = ButtonBindSettings.DataUpdateThreadTimeout;
            ConsUpdateThreadTimeout.Value = ButtonBindSettings.ConsUpdateThreadTimeout;
        }   
        public  void Refresh(string _Key)
        {
            string MessageText = "";

            try
            {
                JObject JSONSettingsdata = JObject.Parse(File.ReadAllText(LMURepairAndRefuelData.path));
                ButtonBindSettings.Clock_Format24 = JSONSettingsdata["Clock_Format24"] != null ? (bool)JSONSettingsdata["Clock_Format24"] : false;
                ButtonBindSettings.RealTimeClock = JSONSettingsdata["RealTimeClock"] != null ? (bool)JSONSettingsdata["RealTimeClock"] : false;
                ButtonBindSettings.GetMemoryDataThreadTimeout = JSONSettingsdata["GetMemoryDataThreadTimeout"] != null ? (int)JSONSettingsdata["GetMemoryDataThreadTimeout"] : 50;
                ButtonBindSettings.DataUpdateThreadTimeout = JSONSettingsdata["DataUpdateThreadTimeout"] != null ? (int)JSONSettingsdata["DataUpdateThreadTimeout"] : 400;
                ButtonBindSettings.ConsUpdateThreadTimeout = JSONSettingsdata["ConsUpdateThreadTimeout"] != null ? (int)JSONSettingsdata["ConsUpdateThreadTimeout"] : 100;
            }
            catch { }
            base.Dispatcher.InvokeAsync(delegate
            {
                
               
                lock (clock_format24)
                {
                    clock_format24.IsChecked = ButtonBindSettings.Clock_Format24;

                }
                lock (RealTimeClock)
                {
                    RealTimeClock.IsChecked = ButtonBindSettings.RealTimeClock;

                }

                lock (DataUpdateThreadTimeout)
                {
                    DataUpdateThreadTimeout.Value = ButtonBindSettings.DataUpdateThreadTimeout;

                }

                lock (ConsUpdateThreadTimeout)
                {
                    ConsUpdateThreadTimeout.Value = ButtonBindSettings.ConsUpdateThreadTimeout;

                }

                lock (GetMemoryDataThreadTimeout)
                {
                    GetMemoryDataThreadTimeout.Value = ButtonBindSettings.GetMemoryDataThreadTimeout;

                }

                lock (message_text)
                {
                    message_text.Text = MessageText;

                }
            }
        );
        }

        private void SHSection_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            //Trigger for saving JSON file. Event is fired if you enter or leave the Plugin Settings View or if you close SimHub

            //Saving on leaving Settings View only
            if (!SHSectionPluginOptions.IsVisible)
            {
                try
                {
           
                  
                }
                catch (Exception ext)
                {
                    Logging.Current.Info("INNIT ERROR: " + ext.ToString());
                }


            }
        }

       

        private void refresh_button_Click(object sender, RoutedEventArgs e)
        {
            clock_format24.IsChecked = ButtonBindSettings.Clock_Format24;
            RealTimeClock.IsChecked = ButtonBindSettings.RealTimeClock;
            GetMemoryDataThreadTimeout.Value = ButtonBindSettings.GetMemoryDataThreadTimeout;
            DataUpdateThreadTimeout.Value = ButtonBindSettings.DataUpdateThreadTimeout;
            ConsUpdateThreadTimeout.Value = ButtonBindSettings.ConsUpdateThreadTimeout;
            message_text.Text = "";
        }

        private async Task SaveSettingAsync()
         {
            JObject JSONdata = new JObject(
                   new JProperty("Clock_Format24", ButtonBindSettings.Clock_Format24),
                   new JProperty("RealTimeClock", ButtonBindSettings.RealTimeClock),
                   new JProperty("GetMemoryDataThreadTimeout", ButtonBindSettings.GetMemoryDataThreadTimeout),
                   new JProperty("DataUpdateThreadTimeout", ButtonBindSettings.DataUpdateThreadTimeout),
                   new JProperty("ConsUpdateThreadTimeout", ButtonBindSettings.ConsUpdateThreadTimeout));

            //string settings_path = AccData.path;
            try
            {
                // create/write settings file
                File.WriteAllText(LMURepairAndRefuelData.path, JSONdata.ToString());
                message_text.Text = "Saving config...";
                await Task.Delay(2000);
                message_text.Text = "";
                Logging.Current.Info("Plugin georace.lmuDataPlugin - Settings file saved to : " + System.Environment.CurrentDirectory + "\\" + LMURepairAndRefuelData.path);
            }
            catch
            {
                //A MessageBox creates graphical glitches after closing it. Search another way, maybe using the Standard Log in SimHub\Logs
                //MessageBox.Show("Cannot create or write the following file: \n" + System.Environment.CurrentDirectory + "\\" + AccData.path, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Logging.Current.Error("Plugin georace.lmuDataPlugin - Cannot create or write settings file: " + System.Environment.CurrentDirectory + "\\" + LMURepairAndRefuelData.path);


            }
        }
       
      

        private void clock_format24_Checked(object sender, RoutedEventArgs e)
        {
            ButtonBindSettings.Clock_Format24 = true;
            message_text.Text = "";
            SaveSettingAsync();
        }
        private void clock_format24_unChecked(object sender, RoutedEventArgs e)
        {
            ButtonBindSettings.Clock_Format24 = false;
            message_text.Text = "";
            SaveSettingAsync();
        }

        private void RealTimeClock_Checked(object sender, RoutedEventArgs e)
        {
            ButtonBindSettings.RealTimeClock = true;
            message_text.Text = "";
            SaveSettingAsync();
        }
        private void RealTimeClock_unChecked(object sender, RoutedEventArgs e)
        {
            ButtonBindSettings.RealTimeClock = false;
            message_text.Text = "";
            SaveSettingAsync();
        }

        private void GetMemoryDataThreadTimeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
        {
            if (GetMemoryDataThreadTimeout.Value != null) { 
                ButtonBindSettings.GetMemoryDataThreadTimeout = (int)GetMemoryDataThreadTimeout.Value;
                SaveSettingAsync();
            }
        }

        private void DataUpdateThreadTimeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
        {
            if (DataUpdateThreadTimeout.Value != null) { 
                ButtonBindSettings.DataUpdateThreadTimeout = (int)DataUpdateThreadTimeout.Value;
                SaveSettingAsync();
            }
        }

        private void ConsUpdateThreadTimeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
        {
            if (ConsUpdateThreadTimeout.Value != null)
            {
                ButtonBindSettings.ConsUpdateThreadTimeout = (int)ConsUpdateThreadTimeout.Value;
                SaveSettingAsync();
            }
        }
    }
   public class LMU_EnegryAndFuelCalculation
    {
        public static double lastLapEnegry { get; set; }
        public static int lapIndex = 0;
        public static bool runned = false;
        public static double LastLapUsed = 0;
        public static bool inPit = true;
        public static double AvgOfFive = 0;

    }
    public class ButtonKeyValues
    {
         string _key { get; set; }
         string _value { get; set; }
    }


    public class ButtonBindSettings
    {
        public static bool RealTimeClock { get; set; }
        public static bool Clock_Format24 { get; set; }
        public static int DataUpdateThreadTimeout { get; set; }
        public static int ConsUpdateThreadTimeout { get; set; }
        public static int GetMemoryDataThreadTimeout { get; set; }

    }
}
