using ScottPlot.WPF;
using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        const int bufferCount = 50000; // Number of x-axis points
        const int receiveBufferCount = 400; // The amount of data sent at one time of STM32 Dev Board (Current: 100 per channel, 4 channel)

        bool isReceiving = false;
        bool isFileSaveEnabled = false;
        bool isAppendWriteEnabled = false;

        SerialPort? serialPort;

        List<StreamWriter> streamWriters = new List<StreamWriter>();

        List<List<float>> buffers = new List<List<float>>();
        List<List<float>> plotBuffers = new List<List<float>>();

        List<int> x = Enumerable.Range(1, bufferCount).ToList();

        List<WpfPlot> wpfPlots = new List<WpfPlot>();

        private Thread receiveThread;
        private DispatcherTimer plotTimer;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize buffers & plotBuffers.
            for(int i = 0; i < channelCount; i++)
            {
                buffers.Add(Enumerable.Repeat(-1f, bufferCount).ToList());
                plotBuffers.Add(Enumerable.Repeat(-1f, bufferCount).ToList());
            }

            // Initialize serial port.
            LoadAvailablePorts();
            SerialPortInit();

            // Initialize plotTimer
            plotTimer = new DispatcherTimer();
            plotTimer.Interval = TimeSpan.FromMilliseconds(10);
            plotTimer.Tick += (sender, e) => { RefreshAllWpfPlots(); };

            // Initialize wpfPlots
            wpfPlots.AddRange(new List<WpfPlot>() { wpfPlot_IEPE1, wpfPlot_IEPE2, wpfPlot_IEPE3, wpfPlot_IEPE4});

            // MainWindow Event Handler
            Loaded += (s, e) =>
            {
                // Set configurations of wpfPlots
                for (int i = 0; i < channelCount; i++)
                {
                    wpfPlots[i].Plot.Add.ScatterLine(x, plotBuffers[i]);

                    wpfPlots[i].Plot.Axes.SetLimits(0, bufferCount, 0, 3);
                    wpfPlots[i].Plot.Axes.Bottom.Label.Text = "Ticks";
                    wpfPlots[i].Plot.Axes.Left.Label.Text = "Voltage(V)";
                }
                RefreshAllWpfPlots();
            };

            Closing += (s, e) =>
            {
                StreamWritersClose();
                plotTimer.Stop();
            };
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
                    serialPort.Open();
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
                default:
                    throw new NotImplementedException();
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

        private bool TestSerialPortAvaliableOrNot(SerialPort serialPort)
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
                receiveThread.Join(); // Prevent StreamWriter closing before receiveThread really stopped.
                StreamWritersClose();
                serialPort.Close();
            }
        }

        private void RefreshUIComponents()
        {
            checkBox_SaveToFiles.IsEnabled = !isReceiving;
            button_DeleteSavedFiles.IsEnabled = !isReceiving;
            button_StartReceive.IsEnabled = !isReceiving;
            button_StopReceive.IsEnabled = isReceiving;
            comboBox_AutoStop.IsEnabled = !isReceiving;
        }

        private void ReceiveLoop(object? obj)
        {
            while (isReceiving)
            {
                byte[] buffer = new byte[4 * receiveBufferCount];
                if(serialPort.BytesToRead >= buffer.Length)
                {
                    serialPort.Read(buffer, 0, buffer.Length);

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
                                ReplaceLastMinusOne(buffers[i], value);
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
        }

        private void RefreshAllWpfPlots()
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