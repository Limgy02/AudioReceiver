using ScottPlot.WPF;
using System.Buffers;
using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AudioReceiver
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // User Configuration
        const int channelCount = 4; // Number of channels
        const int receiveBufferCount = 400; // The amount of data sent at one time of STM32 Dev Board (Current: 100 per channel, 4 channel)

        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000; // USB Device Connect
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004; // USB Device Disconnect

        bool isReceiving = false;
        bool isFileSaveEnabled = false;
        bool isAppendWriteEnabled = false;

        int bufferCount; // Number of x-axis points

        SerialPort? serialPort;

        List<StreamWriter> streamWriters = new List<StreamWriter>();

        List<List<float>> buffers = new List<List<float>>();
        List<List<float>> plotBuffers = new List<List<float>>();

        List<int> x = new List<int>();

        List<WpfPlot> wpfPlots = new List<WpfPlot>();

        private Thread? receiveThread;
        private DispatcherTimer plotTimer;

        enum WpfPlotsRefreshType
        {
            SetAxesLimits,
            DoNotSetAxesLimits
        }

        public MainWindow()
        {
            InitializeComponent();

            // Initialize bufferCount
            bufferCount = Convert.ToInt32(((ComboBoxItem)comboBox_BufferCount.SelectedItem).Content.ToString());

            // Initialize x, buffers & plotBuffers.
            BuffersInit();

            // Initialize serial port.
            LoadAvailablePorts();
            SerialPortInit();

            // Initialize plotTimer
            plotTimer = new DispatcherTimer();
            plotTimer.Interval = TimeSpan.FromMilliseconds(10);
            plotTimer.Tick += (sender, e) => { RefreshAllWpfPlots(WpfPlotsRefreshType.DoNotSetAxesLimits); };

            // Initialize wpfPlots
            wpfPlots.AddRange(new List<WpfPlot>() { wpfPlot_IEPE1, wpfPlot_IEPE2, wpfPlot_IEPE3, wpfPlot_IEPE4 });

            // MainWindow Event Handler

            // Add message hook for monitor of USB devices.
            SourceInitialized += (s, e) =>
            {
                var windowInteropHelper = new WindowInteropHelper(this);
                var hwnd = windowInteropHelper.Handle;

                HwndSource source = HwndSource.FromHwnd(hwnd);
                source.AddHook(Hook);
            };

            Loaded += (s, e) =>
            {
                // Add scatter line for wpfPlots
                AddScatterLineForWpfPlots();

                // Set configurations of wpfPlots
                for (int i = 0; i < channelCount; i++)
                {
                    wpfPlots[i].Plot.Axes.SetLimits(0, bufferCount, 0, 3);
                    wpfPlots[i].Plot.Axes.Bottom.Label.Text = "Ticks";
                    wpfPlots[i].Plot.Axes.Left.Label.Text = "Voltage(V)";
                }
                RefreshAllWpfPlots(WpfPlotsRefreshType.SetAxesLimits);

                // Add event handler for ComboBoxs, prevent which SelectionChanged event trigger when MainWindow loading.
                comboBox_PortName.SelectionChanged += Combox_SelectionChanged;
                comboBox_BufferCount.SelectionChanged += Combox_SelectionChanged;
            };

            Closing += (s, e) =>
            {
                StopReceive();
                StreamWritersClose();
            };
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int eventType = wparam.ToInt32();

                switch (eventType)
                {
                    case DBT_DEVICEARRIVAL:
                    case DBT_DEVICEREMOVECOMPLETE:
                        LoadAvailablePorts();
                        break;
                    default:
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void Button_Click(object sender, EventArgs e)
        {
            switch (((Button)sender).Name)
            {
                case "button_StartReceive":
                    if (!TestSerialPortAvaliableOrNot(serialPort))
                        return;
                    isReceiving = true;
                    if (checkBox_SaveToFiles.IsChecked == true)
                        StreamWritersInit();
                    serialPort!.Open();
                    receiveThread = new Thread(ReceiveLoop);
                    receiveThread.IsBackground = true;
                    receiveThread.Start();
                    plotTimer.Start();

                    // Auto Stop
                    if(comboBox_AutoStop.SelectedIndex != 0)
                    {
                        DispatcherTimer timer = new DispatcherTimer();
                        switch (comboBox_AutoStop.SelectedIndex)
                        {
                            case 1:
                                timer.Interval = TimeSpan.FromSeconds(1);
                                break;
                            case 2:
                                timer.Interval = TimeSpan.FromSeconds(5);
                                break;
                            case 3:
                                timer.Interval = TimeSpan.FromSeconds(10);
                                break;
                            case 4:
                                timer.Interval = TimeSpan.FromSeconds(30);
                                break;
                            default:
                                throw new NotImplementedException();

                        }
                        timer.Tick += (cs, ce) =>
                        {
                            StopReceive();
                            timer.Stop();
                            RefreshUIComponents();
                        };
                        timer.Start();
                    }
                    break;
                case "button_StopReceive":
                    StopReceive();
                    break;
                case "button_DeleteSavedFiles":
                    var result = MessageBox.Show("Are you sure to delete saved files?", "Qusetion", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        bool success = true;
                        for (int i = 1; i <= channelCount; i++)
                        {
                            try
                            {
                                File.Delete($"output{i}.txt");
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                success = false;
                            }
                        }
                        if (success)
                            MessageBox.Show("All files deleted successfully.", "Message", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    break;
                case "button_RefreshPortNames":
                    LoadAvailablePorts();
                    break;
                case "button_WpfPlot_IEPE1_AxesLimits_Reset":
                case "button_WpfPlot_IEPE2_AxesLimits_Reset":
                case "button_WpfPlot_IEPE3_AxesLimits_Reset":
                case "button_WpfPlot_IEPE4_AxesLimits_Reset":
                    WpfPlot currentWpfPlot = wpfPlots[Convert.ToInt32(((Button)sender).Tag)];
                    currentWpfPlot.Plot.Axes.SetLimitsX(0, bufferCount);
                    currentWpfPlot.Plot.Axes.SetLimitsY(0, 3);
                    currentWpfPlot.Refresh();
                    break;
                default:
                    throw new NotImplementedException();
            }
            RefreshUIComponents();
        }

        private void CheckBox_IsCheckedStatusChanged(object sender, RoutedEventArgs e)
        {
            switch (((CheckBox)sender).Name)
            {
                case "checkBox_SaveToFiles":
                    isFileSaveEnabled = checkBox_SaveToFiles.IsChecked ?? false;
                    break;
                case "checkBox_AppendWrite":
                    isAppendWriteEnabled = checkBox_AppendWrite.IsChecked ?? false;
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private void Combox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch (((ComboBox)sender).Name)
            {
                case "comboBox_PortName":
                    SerialPortInit();
                    break;
                case "comboBox_BufferCount":
                    bufferCount = Convert.ToInt32(((ComboBoxItem)comboBox_BufferCount.SelectedItem).Content.ToString());
                    BuffersInit();
                    AddScatterLineForWpfPlots();
                    RefreshAllWpfPlots(WpfPlotsRefreshType.SetAxesLimits);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private void BuffersInit()
        {
            x = Enumerable.Range(1, bufferCount).ToList();

            buffers.Clear();
            plotBuffers.Clear();
            for (int i = 0; i < channelCount; i++)
            {
                buffers.Add(Enumerable.Repeat(-1f, bufferCount).ToList());
                plotBuffers.Add(Enumerable.Repeat(-1f, bufferCount).ToList());
            }
        }

        private void AddScatterLineForWpfPlots()
        {
            for (int i = 0; i < channelCount; i++)
            {
                wpfPlots[i].Plot.Clear();
                wpfPlots[i].Plot.Add.ScatterLine(x, plotBuffers[i]);
            }
        }

        private void SerialPortInit()
        {
            string? portName = comboBox_PortName.SelectedItem?.ToString();
            if (Regex.IsMatch(portName ?? "", @"^COM\d+$"))
            {
                serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One);
                serialPort.ReadBufferSize = 1024 * 64;
            }
            else
                serialPort = null;
        }

        private void LoadAvailablePorts()
        {
            comboBox_PortName.Items.Clear();
            foreach(string port in SerialPort.GetPortNames())
            {
                comboBox_PortName.Items.Add(port);
            }
            if (comboBox_PortName.Items.Count > 0)
            {
                comboBox_PortName.SelectedIndex = 0;
            }
        }

        private bool TestSerialPortAvaliableOrNot(SerialPort? serialPort)
        {
            if(serialPort is null)
            {
                MessageBox.Show("Serial Port have not been initialized, " +
                    "please check device connection status and " +
                    "select correct port name.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            bool isSerialPortAvaliable = true;
            try
            {
                serialPort.Open();
            }
            catch (Exception ex)
            {
                isSerialPortAvaliable = false;
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            if (isSerialPortAvaliable)
                serialPort.Close();
            return isSerialPortAvaliable;
        }

        private void StopReceive()
        {
            if(isReceiving)
            {
                isReceiving = false;
                if(Thread.CurrentThread.ManagedThreadId != receiveThread?.ManagedThreadId)
                    receiveThread!.Join(); // Prevent StreamWriter closing before receiveThread really stopped.
                StreamWritersClose();
                serialPort!.Close();
                plotTimer.Stop();
            }
        }

        private void RefreshUIComponents()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                checkBox_SaveToFiles.IsEnabled = !isReceiving;
                checkBox_AppendWrite.IsEnabled = !isReceiving;

                // Buttons
                button_DeleteSavedFiles.IsEnabled = !isReceiving;
                button_StartReceive.IsEnabled = !isReceiving;
                button_StopReceive.IsEnabled = isReceiving;
                button_RefreshPortNames.IsEnabled = !isReceiving;

                // ComboBoxs
                comboBox_AutoStop.IsEnabled = !isReceiving;
                comboBox_BufferCount.IsEnabled = !isReceiving;
                comboBox_PortName.IsEnabled = !isReceiving;
            }));
        }

        private void ReceiveLoop(object? obj)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4 * receiveBufferCount);
            while (isReceiving)
            {
                try
                {
                    if (serialPort!.BytesToRead < buffer.Length)
                        continue;
                    serialPort.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Serial port read failed, please check connection of the device.\r\n\r\n" +
                        $"Message:\r\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StopReceive();
                    RefreshUIComponents();
                }

                int index = 0;
                for (int i = 0; i < buffer.Length; i += 4)
                {
                    if (i + 4 <= buffer.Length)
                    {
                        float value = BitConverter.ToSingle(buffer, i);

                        int currentChannel = index % 4;
                        if (buffers[currentChannel].Count >= bufferCount)
                        {
                            buffers[currentChannel].Insert(0, value);
                            buffers[currentChannel].RemoveAt(buffers[currentChannel].Count - 1);
                        }
                        else
                        {
                            ReplaceLastMinusOne(buffers[currentChannel], value);
                        }
                        if (isFileSaveEnabled)
                        {
                            streamWriters[currentChannel].Write($"{value}\r\n");
                        }
                    }
                    index++;
                }
            }
        }

        private void RefreshAllWpfPlots(WpfPlotsRefreshType refreshType)
        {
            for(int i = 0; i < channelCount; i++)
            {
                // Use plotBuffer.Clear() and plotBuffer.AddRange(buffer) method instead of plotBuffer = buffer.ToList() method,
                // otherwise wpfPlot will not refresh.
                //
                // Only use wpfPlot.Refresh() method consume a lot of time and cause lag in different channel
                // depend on the order of refresh.

                plotBuffers[i].Clear();
                plotBuffers[i].AddRange(buffers[i]);

                if(refreshType == WpfPlotsRefreshType.SetAxesLimits)
                    wpfPlots[i].Plot.Axes.SetLimitsX(0, bufferCount);

                wpfPlots[i].Refresh();
            }
        }

        private void ReplaceLastMinusOne(List<float> list, float value)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == -1f)
                {
                    list[i] = value;
                    break;
                }
            }
        }

        private void StreamWritersInit()
        {
            for(int i = 0; i < channelCount; i++)
            {
                streamWriters.Add(new StreamWriter($"output{i + 1}.txt", append: isAppendWriteEnabled));
            }
        }

        private void StreamWritersClose()
        {
            for(int i = 0; i < streamWriters.Count; i++)
            {
                streamWriters[i]?.Close();
            }
            streamWriters.Clear();
        }
    }
}