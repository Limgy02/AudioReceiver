using ScottPlot.WPF;
using System.IO;
using System.IO.Ports;
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
        bool isReceiving = false;
        bool isFileSaveEnabled = false;

        SerialPort serialPort;
        StreamWriter writer1, writer2, writer3, writer4;

        const int channelCount = 4;
        const int bufferCount = 50000;
        const int receiveBufferCount = 400;

        List<float> buffer1 = Enumerable.Repeat(-1f, bufferCount).ToList();
        List<float> buffer2 = Enumerable.Repeat(-1f, bufferCount).ToList();
        List<float> buffer3 = Enumerable.Repeat(-1f, bufferCount).ToList();
        List<float> buffer4 = Enumerable.Repeat(-1f, bufferCount).ToList();

        List<float> plotBuffer1 = Enumerable.Repeat(-1f, bufferCount).ToList();
        List<float> plotBuffer2 = Enumerable.Repeat(-1f, bufferCount).ToList();
        List<float> plotBuffer3 = Enumerable.Repeat(-1f, bufferCount).ToList();
        List<float> plotBuffer4 = Enumerable.Repeat(-1f, bufferCount).ToList();

        List<int> x = Enumerable.Range(1, bufferCount).ToList();

        private Thread receiveThread;
        private DispatcherTimer plotTimer;

        public MainWindow()
        {
            InitializeComponent();

            serialPort = new SerialPort("COM4", 115200, Parity.None, 8, StopBits.One);
            serialPort.ReadBufferSize = 1024 * 64;

            plotTimer = new DispatcherTimer();
            plotTimer.Interval = TimeSpan.FromMilliseconds(10);
            plotTimer.Tick += (sender, e) => { RefreshAllWpfPlots(); };

            Loaded += (s, e) =>
            {
                wpfPlot_IEPE1.Plot.Add.ScatterLine(x, plotBuffer1);
                wpfPlot_IEPE2.Plot.Add.ScatterLine(x, plotBuffer2);
                wpfPlot_IEPE3.Plot.Add.ScatterLine(x, plotBuffer3);
                wpfPlot_IEPE4.Plot.Add.ScatterLine(x, plotBuffer4);

                List<WpfPlot> wpfPlots = GetAllWpfPlots(MainGrid);
                foreach(var wpfPlot in wpfPlots)
                {
                    wpfPlot.Plot.Axes.SetLimits(0, bufferCount, 0, 3);
                    wpfPlot.Plot.Axes.Bottom.Label.Text = "Ticks";
                    wpfPlot.Plot.Axes.Left.Label.Text = "Voltage(V)";
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
                        bool success = false;
                        try
                        {
                            for(int i = 1; i <= 4; i++)
                            {
                                File.Delete($"output{i}.txt");
                            }
                            success = true;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        if (success)
                            MessageBox.Show("All files deleted successfully.", "Message", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
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
                default:
                    throw new NotImplementedException();
            }
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
                            switch (index % 4)
                            {
                                case 0:
                                    if (buffer1.Count >= bufferCount)
                                    {
                                        buffer1.Insert(0, value);
                                        buffer1.RemoveAt(buffer1.Count - 1);
                                    }
                                    else
                                    {
                                        ReplaceLastMinusOne(buffer1, value);
                                    }
                                    if (isFileSaveEnabled)
                                        writer1.Write($"{value}\r\n");
                                    break;
                                case 1:
                                    if (buffer2.Count >= bufferCount)
                                    {
                                        buffer2.Insert(0, value);
                                        buffer2.RemoveAt(buffer2.Count - 1);
                                    }
                                    else
                                    {
                                        ReplaceLastMinusOne(buffer2, value);
                                    }
                                    if (isFileSaveEnabled)
                                        writer2.Write($"{value}\r\n");
                                    break;
                                case 2:
                                    if (buffer3.Count >= bufferCount)
                                    {
                                        buffer3.Insert(0, value);
                                        buffer3.RemoveAt(buffer3.Count - 1);
                                    }
                                    else
                                    {
                                        ReplaceLastMinusOne(buffer3, value);
                                    }
                                    if (isFileSaveEnabled)
                                        writer3.Write($"{value}\r\n");
                                    break;
                                case 3:
                                    if (buffer4.Count >= bufferCount)
                                    {
                                        buffer4.Insert(0, value);
                                        buffer4.RemoveAt(buffer4.Count - 1);
                                    }
                                    else
                                    {
                                        ReplaceLastMinusOne(buffer4, value);
                                    }
                                    if (isFileSaveEnabled)
                                        writer4.Write($"{value}\r\n");
                                    break;
                                default:
                                    break;
                            }
                        }
                        index++;
                    }
                }
            }
        }

        public List<WpfPlot> GetAllWpfPlots(DependencyObject parent)
        {
            List<WpfPlot> wpfPlots = new List<WpfPlot>();

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is WpfPlot plot)
                    wpfPlots.Add(plot);

                wpfPlots.AddRange(GetAllWpfPlots(child));
            }

            return wpfPlots;
        }

        private void RefreshAllWpfPlots()
        {
            // Use plotBuffer.Clear() and plotBuffer.AddRange(buffer) method instead of plotBuffer = buffer.ToList() method,
            // otherwise wpfPlot will not refresh.
            //
            // Only use wpfPlot.Refresh() method consume a lot of time and cause lag in different channel
            // depend on the order of refresh.

            plotBuffer1.Clear();
            plotBuffer1.AddRange(buffer1);
            plotBuffer2.Clear();
            plotBuffer2.AddRange(buffer2);
            plotBuffer3.Clear();
            plotBuffer3.AddRange(buffer3);
            plotBuffer4.Clear();
            plotBuffer4.AddRange(buffer4);

            wpfPlot_IEPE1.Refresh();
            wpfPlot_IEPE2.Refresh();
            wpfPlot_IEPE3.Refresh();
            wpfPlot_IEPE4.Refresh();
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
            writer1 = new StreamWriter("output1.txt", append: true);
            writer2 = new StreamWriter("output2.txt", append: true);
            writer3 = new StreamWriter("output3.txt", append: true);
            writer4 = new StreamWriter("output4.txt", append: true);
        }

        private void StreamWritersClose()
        {
            writer1?.Close();
            writer2?.Close();
            writer3?.Close();
            writer4?.Close();
        }
    }
}