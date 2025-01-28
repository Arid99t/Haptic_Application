using System;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Text;
using System.Net;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave.SampleProviders;

namespace PlayGroundApp_3
{
    public partial class MainForm : Form
    {
        private SerialPort serialPort;
        private UdpClient udpClient;
        private const int UdpSendPort = 8889; // Unity's listening port
        private const int UdpReceivePort = 8890; // Application's receiving port
        private const int UdpMaterialConstantPort = 8894;
        private const int UdpMaxDeformationPort = 8895; //for Spring constant changes
        private bool isConnected = false;
        private System.Windows.Forms.Timer updateTimer;
        private System.Windows.Forms.Timer vibrationTimer;
        private UdpClient udpMaterialConstantClient;
        private int currentRound = 0;
        private Label labelMaxDeformation;


        private string[] wavFilePaths = new string[3];
        private Label[] wavFileLabels = new Label[3];

        private IWavePlayer outputDevice;
        private AudioFileReader[] audioFileReaders = new AudioFileReader[3];
        private VolumeSampleProvider[] volumeProviders = new VolumeSampleProvider[3];
        private WaveChannel32[] volumeStreams = new WaveChannel32[3];

        private const float VIBRATION_SMOOTHING_FACTOR = .5f;
        private const int FORCE_MIN = 0;
        private const int FORCE_MAX = 1023;
        private const float VOLUME_MULTIPLIER = 9.0f; //  for VOLUME !!!!!!

        private bool isPlaying = false;
        private int lastForce = 0;
        private float currentVibrationIntensity = 0f;
        private float playbackSpeed = 1f;

        private TrackBar trackBarProgression;
        private TrackBar trackBarIntensity;

        private const int MinTickInterval = 100; // Minimum interval in milliseconds (fastest)
        private const int MaxTickInterval = 500; // Maximum interval in milliseconds (slowest)




        private const float MAX_PLAYBACK_RATE = 2.0f;
        private const float MIN_PLAYBACK_RATE = 0.5f;
        private const int UPDATE_INTERVAL_MS = 20;
        private DateTime lastUpdateTime = DateTime.Now;

        private const int PRESSURE_CHANGE_THRESHOLD = 20;
        private const int PRESSURE_CHANGE_TIME_WINDOW = 20; // in milliseconds
        private int lastSignificantPressure;
        private DateTime lastSignificantPressureTime;
        private float lastSignificantIntensity;
        
        private int[][] latinSquare = new int[][]
        {
            new int[] { 1, 2, 3 },
            new int[] { 2, 3, 1 },
            new int[] { 3, 1, 2 }
        };
        private int currentLatinSquareRow = 0;
        private int currentLatinSquareColumn = 0;
        private UdpClient udpMaxDeformationClient;
        private float maxDeformation = 0f;


        public MainForm()
        {
            InitializeComponent();
            udpMaxDeformationClient = new UdpClient(UdpMaxDeformationPort);
            udpMaxDeformationClient.BeginReceive(ReceiveMaxDeformationCallback, null);
            labelMaxDeformation = new Label
            {
                Location = new Point(10, 340), // Adjust the position as needed
                Size = new Size(200, 20),      // Adjust the size as needed
                Text = $"Max Deformation: {maxDeformation:F2}"
            };
            this.Controls.Add(labelMaxDeformation);

            PopulateComPorts();
            InitializeUdpClients();
            InitializeTimer();
            InitializeVibrationTimer();
            InitializeMaterialConstant();

            Button loadWavButton = new Button
            {
                Text = "Load WAV",
                Location = new Point(10, 220),
                Size = new Size(100, 30)
            };
            loadWavButton.Click += LoadWavButton_Click;
            this.Controls.Add(loadWavButton);

            for (int i = 0; i < 3; i++)
            {
                wavFileLabels[i] = new Label
                {
                    Location = new Point(10, 260 + (i * 20)),
                    Size = new Size(400, 20),
                    Text = $"File {i + 1}: Not loaded"
                };
                this.Controls.Add(wavFileLabels[i]);
            }

            trackBarProgression = new TrackBar
            {
                Location = new Point(150, 180),
                Width = 400,
                Maximum = 1000,
                Minimum = 0,
                TickFrequency = 5
            };
            this.Controls.Add(trackBarProgression);

            trackBarIntensity = new TrackBar
            {
                Location = new Point(150, 220),
                Width = 400,
                Maximum = 500,
                Minimum = 0,
                TickFrequency = 5
            };
            this.Controls.Add(trackBarIntensity);

            InitializeAudioDevice();
        }

        private void InitializeUdpClients()
        {
            // Main UDP client
            udpClient = new UdpClient(UdpReceivePort);
            udpClient.BeginReceive(ReceiveCallback, null);

            // Material constant UDP client
            udpMaterialConstantClient = new UdpClient(UdpMaterialConstantPort);
            udpMaterialConstantClient.BeginReceive(ReceiveMaterialConstantCallback, null);

            UpdatePortDisplay();
        }



        private void ReceiveMaterialConstantCallback(IAsyncResult ar)
        {
            try
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, UdpMaterialConstantPort);
                byte[] data = udpMaterialConstantClient.EndReceive(ar, ref endPoint);
                string message = Encoding.ASCII.GetString(data);

                Console.WriteLine($"Received message on Material Constant port: {message}");

                if (message.StartsWith("ROUND_COMPLETED:"))
                {
                    if (int.TryParse(message.Substring(16), out int completedRound))
                    {
                        Console.WriteLine($"Parsed completed round: {completedRound}");
                        this.BeginInvoke(new Action(() => UpdateRound(completedRound)));
                    }
                    else
                    {
                        Console.WriteLine($"Failed to parse completed round from message: {message}");
                    }
                }
                else
                {
                    Console.WriteLine($"Received unexpected message format: {message}");
                }

                udpMaterialConstantClient.BeginReceive(ReceiveMaterialConstantCallback, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP Material Constant Receive error: {ex.Message}");
            }
        }
        private void SelectLatinSquareRow()
        {
            Random random = new Random();
            currentLatinSquareRow = random.Next(0, latinSquare.Length);
            currentLatinSquareColumn = 0;
            Console.WriteLine($"Selected Latin square row: {currentLatinSquareRow + 1}");
        }

        private void LoadWavButton_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < 3; i++)
            {
                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "WAV files (*.wav)|*.wav",
                    Title = $"Select WAV file {i + 1}"
                };

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    wavFilePaths[i] = openFileDialog.FileName;
                    wavFileLabels[i].Text = $"File {i + 1}: {Path.GetFileName(wavFilePaths[i])}";
                }
                else
                {
                    wavFilePaths[i] = null;
                    wavFileLabels[i].Text = $"File {i + 1}: Not loaded";
                }
            }
            InitializeAudioDevice();
        }

        private void InitializeMaterialConstant()
        {
            try
            {
                SelectLatinSquareRow();
                currentRound = 1;
                int newConstant = GetNextMaterialConstant();

                // Ensure the constant is within valid range (1-3)
                newConstant = Math.Max(1, Math.Min(3, newConstant));

                // Set the combo box index (0-based, so subtract 1)
                this.BeginInvoke(new Action(() => {
                    comboBoxMaterialConstant.SelectedIndex = newConstant;
                    Console.WriteLine($"Initial material constant set to: {newConstant}");
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in InitializeMaterialConstant: {ex.Message}");
            }
        }


        private void InitializeAudioDevice()
        {
            DisposeAudioDevices();

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Console.WriteLine($"Default audio device: {defaultDevice.FriendlyName}");

                outputDevice = new WasapiOut(defaultDevice, AudioClientShareMode.Shared, true, 20);
                Console.WriteLine($"Selected audio device: {defaultDevice.FriendlyName}");

                for (int i = 0; i < 3; i++)
                {
                    if (!string.IsNullOrEmpty(wavFilePaths[i]))
                    {
                        audioFileReaders[i] = new AudioFileReader(wavFilePaths[i]);
                        volumeStreams[i] = new WaveChannel32(audioFileReaders[i]);
                        Console.WriteLine($"Loaded WAV file {i + 1}: {wavFilePaths[i]}");
                    }
                    else
                    {
                        audioFileReaders[i] = null;
                        volumeStreams[i] = null;
                        Console.WriteLine($"WAV file {i + 1} not loaded.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing audio device: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }
        }

        private void DisposeAudioDevices()
        {
            if (outputDevice != null)
            {
                outputDevice.Stop();
                outputDevice.Dispose();
                outputDevice = null;
            }

            for (int i = 0; i < 3; i++)
            {
                if (volumeStreams[i] != null)
                {
                    volumeStreams[i].Dispose();
                    volumeStreams[i] = null;
                }

                if (audioFileReaders[i] != null)
                {
                    audioFileReaders[i].Dispose();
                    audioFileReaders[i] = null;
                }
            }
        }

        private void StartPlayback(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= 3 || volumeStreams[trackIndex] == null)
                return;

            try
            {
                const float MIN_VOLUME_THRESHOLD = 0.01f;
                if (volumeStreams[trackIndex].Volume < MIN_VOLUME_THRESHOLD)
                {
                    return;
                }

                outputDevice?.Stop();

                if (outputDevice == null || !(outputDevice is DirectSoundOut))
                {
                    outputDevice?.Dispose();
                    outputDevice = new DirectSoundOut(20);
                }

                // Reset the position to start
                audioFileReaders[trackIndex].Position = 0;

                outputDevice.Init(volumeStreams[trackIndex]);
                outputDevice.Play();
                isPlaying = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting playback: {ex.Message}");
            }
        }

        private void StopPlayback()
        {
            if (isPlaying)
            {
                outputDevice.Stop();
                isPlaying = false;
                Console.WriteLine("Stopped playback");
            }
        }

        

        private void UpdatePortDisplay()
        {
            labelPort.Text = $"UDP Listening Port: {UdpReceivePort}, Sending Port: {UdpSendPort}";
        }

        private void InitializeTimer()
        {
            updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 16; // Update at approximately 60 FPS
            updateTimer.Tick += UpdateTimer_Tick;
            updateTimer.Start();
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            // Update track bar progression based on current playback position
            int selectedMaterialConstant = comboBoxMaterialConstant.SelectedIndex - 1;
            if (selectedMaterialConstant >= 0 && selectedMaterialConstant < 3 && audioFileReaders[selectedMaterialConstant] != null)
            {
                float progression = (float)audioFileReaders[selectedMaterialConstant].CurrentTime.TotalSeconds / (float)audioFileReaders[selectedMaterialConstant].TotalTime.TotalSeconds;
                trackBarProgression.Value = (int)(progression * trackBarProgression.Maximum);
            }
        }


        private void InitializeVibrationTimer()
        {
            vibrationTimer = new System.Windows.Forms.Timer();
            vibrationTimer.Interval = 20;//Check every 20ms
            vibrationTimer.Tick += VibrationTimer_Elapsed;
            vibrationTimer.Start();
        }

        private void VibrationTimer_Elapsed(object sender, EventArgs e)
        {
            int currentForce = trackBarPressure.Value;
            UpdateVibrationIntensity(currentForce);
        }

        private void UpdateVibrationIntensity(int currentForce)
        {
            float scalingFactor = GetPressureScalingFactor();
            int adjustedForce = (int)(currentForce * scalingFactor);
            adjustedForce = Math.Max(0, Math.Min(adjustedForce, FORCE_MAX));

            int selectedMaterialConstant = comboBoxMaterialConstant.SelectedIndex - 1;
            if (selectedMaterialConstant < 0 || selectedMaterialConstant >= 3 || volumeStreams[selectedMaterialConstant] == null)
            {
                StopPlayback();
                return;
            }

            int forceDelta = adjustedForce - lastForce;
            bool isSignificantChange = Math.Abs(adjustedForce - lastSignificantPressure) > PRESSURE_CHANGE_THRESHOLD;
            bool isWithinTimeWindow = (DateTime.Now - lastSignificantPressureTime).TotalMilliseconds <= PRESSURE_CHANGE_TIME_WINDOW;

            if (isSignificantChange || isWithinTimeWindow)
            {
                float normalizedForce = (float)adjustedForce / FORCE_MAX;
                currentVibrationIntensity = normalizedForce;

                if (!isPlaying)
                {
                    StartPlayback(selectedMaterialConstant);
                }

                if (audioFileReaders[selectedMaterialConstant] != null)
                {
                    // Calculate desired position based on force
                    float positionPercentage = normalizedForce;

                    // For softer materials, increase the position change to create a faster effect
                    if (maxDeformation >= 0.5f)
                    {
                        float speedMultiplier = 1.0f + ((maxDeformation - 0.5f) * 3.0f);
                        positionPercentage = positionPercentage * speedMultiplier;
                    }

                    // Handle looping
                    while (positionPercentage >= 1.0f)
                    {
                        positionPercentage -= 1.0f;
                    }

                    long position = (long)(audioFileReaders[selectedMaterialConstant].Length * positionPercentage);

                    // If we're near the end of the file, loop back to start
                    if (position >= audioFileReaders[selectedMaterialConstant].Length - 4410) // 4410 samples = 0.1sec at 44.1kHz
                    {
                        position = 0;
                    }

                    audioFileReaders[selectedMaterialConstant].Position = position;

                    // Set volume based on force and material softness
                    float volumeLevel = normalizedForce * VOLUME_MULTIPLIER;
                    if (maxDeformation >= 0.5f)
                    {
                        float volumeMultiplier = 1.0f + ((maxDeformation - 0.5f) * 1.5f);
                        volumeLevel *= volumeMultiplier;
                    }
                    volumeStreams[selectedMaterialConstant].Volume = Math.Min(volumeLevel, VOLUME_MULTIPLIER);
                }

                lastSignificantPressure = adjustedForce;
                lastSignificantPressureTime = DateTime.Now;
                lastSignificantIntensity = currentVibrationIntensity;
            }
            else
            {
                if (volumeStreams[selectedMaterialConstant] != null)
                {
                    volumeStreams[selectedMaterialConstant].Volume = 0;
                }
            }

            int scaledIntensity = (int)(currentVibrationIntensity * 100);
            UpdateIntensityUI(scaledIntensity);
            SendVibrationTick(scaledIntensity);

            lastForce = adjustedForce;
        }





        private float CalculateVibrationIntensity(int force, int forceDelta)
        {
            // Simple linear scaling of force
            float normalizedForce = force / (float)FORCE_MAX;
            return Math.Min(normalizedForce, 1.0f); // Ensure it doesn't exceed 1.0
        }

        private void AdjustAudioProperties(int trackIndex, int force, int forceDelta)
        {
            if (audioFileReaders[trackIndex] == null) return;

            // Adjust playback rate based on force change rate
            float playbackRate = 1.0f + (Math.Abs((float)forceDelta / FORCE_MAX));
            playbackRate = Math.Max(MIN_PLAYBACK_RATE, Math.Min(MAX_PLAYBACK_RATE, playbackRate));

            // Calculate position based on force
            long newPosition = (long)(audioFileReaders[trackIndex].Length * ((float)force / FORCE_MAX));

            // Apply audio adjustments
            ApplyAudioAdjustments(trackIndex, playbackRate, newPosition);
        }

        private void ApplyAudioAdjustments(int trackIndex, float playbackRate, long newPosition)
        {
            if (audioFileReaders[trackIndex] != null)
            {
                // Set new position
                audioFileReaders[trackIndex].Position = newPosition;

                // Note: NAudio doesn't directly support changing playback speed
                // If you need this functionality, you might need to implement a custom ISampleProvider
                // that can adjust the playback speed

                Console.WriteLine($"Track {trackIndex}: Rate {playbackRate:F2}, Position {newPosition}");
            }
        }




        private float CalculateForceChange(int currentForce)
        {
            float change = Math.Abs(currentForce - lastForce);
            return change > 0 ? change : 0;
        }

        private float CalculateVolume(int currentForce)
        {
            // Normalize the force to a 0-1 range
            float normalizedForce = (float)currentForce / FORCE_MAX;

            // Apply a non-linear curve for more natural response (optional)
            float curvedResponse = (float)Math.Pow(normalizedForce, 1.5);

            // Ensure the volume is within the 0-1 range
            return Math.Max(0, Math.Min(1, curvedResponse));
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, UdpReceivePort);
                byte[] data = udpClient.EndReceive(ar, ref ep);
                string message = Encoding.ASCII.GetString(data);
                Console.WriteLine($"Received UDP: {message}");

                if (message.StartsWith("PRESSURE:"))
                {
                    if (float.TryParse(message.Substring(9), out float pressureValue))
                    {
                        this.BeginInvoke(new Action(() => UpdatePressureAndFingerDistanceUI((int)pressureValue)));
                    }
                }
                else if (message.StartsWith("ROUND_COMPLETED:"))
                {
                    if (int.TryParse(message.Substring(15), out int completedRound))
                    {
                        this.BeginInvoke(new Action(() => UpdateRound(completedRound)));
                    }
                }

                udpClient.BeginReceive(ReceiveCallback, null);
            }
            catch (ObjectDisposedException)
            {
                // UDP client has been closed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP Receive error: {ex.Message}");
            }
        }

        private void UpdateRound(int completedRound)
        {
            Console.WriteLine($"UpdateRound called with completedRound: {completedRound}");
            Console.WriteLine($"Current material constant before update: {comboBoxMaterialConstant.SelectedIndex}");

            currentRound = completedRound + 1;

            if (currentRound == 4 || currentRound == 7)  // or any other condition to change material constant
            {
                Console.WriteLine($"Condition met to change material constant (currentRound: {currentRound})");

                // Select a random index between 1 and 3 (excluding 0)
                int newConstantIndex = GetNextRandomMaterialConstantIndex();
                Console.WriteLine($"New material constant index selected: {newConstantIndex}");

                this.BeginInvoke(new Action(() => {
                    comboBoxMaterialConstant.SelectedIndex = newConstantIndex;  // Set the ComboBox index
                    int newConstant = comboBoxMaterialConstant.SelectedIndex;  // This converts to Material Constant value
                    Console.WriteLine($"Material constant changed to: {newConstant}");

                    StopPlayback();
                    ResetVibrationState();
                    UpdatePressureAndFingerDistanceUI(trackBarPressure.Value);

                    StartPlayback(comboBoxMaterialConstant.SelectedIndex);

                    SendMaterialConstantToUnity(newConstant);

                    Console.WriteLine($"Material constant change applied. Current state: {newConstant}");
                }));
            }
            else
            {
                Console.WriteLine($"Condition not met to change material constant (currentRound: {currentRound})");
            }

            Console.WriteLine($"Current round updated to: {currentRound}/9");
        }

        private int GetNextRandomMaterialConstantIndex()
        {
            List<int> availableIndexes = new List<int> { 1, 2, 3 };  // Valid indexes, excluding 0

            // Remove already used indexes (if tracking is implemented)
            // Example: if you've used 1 and 2, remove them from availableIndexes

            Random random = new Random();
            int selectedIndex = availableIndexes[random.Next(availableIndexes.Count)];

            // Optionally track used indexes to prevent immediate repetition
            // Update your tracking here

            return selectedIndex;
        }



        private int GetNextMaterialConstant()
        {
            int newConstant = latinSquare[currentLatinSquareRow][currentLatinSquareColumn];

            Console.WriteLine($"Raw material constant from Latin Square: {newConstant}");

            // The modulo operation is removed because the Latin Square values are already correct.
            // newConstant = (newConstant - 1) % 3 + 1;

            Console.WriteLine($"Final material constant selected: {newConstant}");
            Console.WriteLine($"Current position: Row {currentLatinSquareRow + 1}, Column {currentLatinSquareColumn + 1}");

            currentLatinSquareColumn++;
            if (currentLatinSquareColumn >= 3)
            {
                currentLatinSquareColumn = 1;
                currentLatinSquareRow = (currentLatinSquareRow) % 3;
            }

            return newConstant; // This will return 1, 2, or 3 directly from the Latin Square
        }
        private void InitializeMaterialConstantComboBox()
        {
            comboBoxMaterialConstant.Items.Clear();
            comboBoxMaterialConstant.Items.Add("Material 1");
            comboBoxMaterialConstant.Items.Add("Material 2");
            comboBoxMaterialConstant.Items.Add("Material 3");
            Console.WriteLine($"ComboBox initialized with {comboBoxMaterialConstant.Items.Count} items");
        }

        private void AdvanceToNextMaterialConstant()
        {
            currentLatinSquareColumn++;
            if (currentLatinSquareColumn >= latinSquare[currentLatinSquareRow].Length)
            {
                currentLatinSquareColumn = 0;
            }
        }

        private void SendMaterialConstantToUnity(int materialConstant)
        {
            if (udpClient != null)
            {
                try
                {
                    string message = $"MATERIAL_CONSTANT:{materialConstant}";
                    byte[] data = Encoding.ASCII.GetBytes(message);
                    udpClient.Send(data, data.Length, "127.0.0.1", 8894);
                    Console.WriteLine($"Sent material constant {materialConstant} to Unity");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending material constant data: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("UDP client is null, cannot send material constant");
            }
        }



        private void PopulateComPorts()
        {
            comboBoxComPorts.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > 0)
            {
                comboBoxComPorts.Items.AddRange(ports);
                comboBoxComPorts.SelectedIndex = 0;
            }
            else
            {
                MessageBox.Show("No COM ports detected.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeSerialPort()
        {
            serialPort = new SerialPort(comboBoxComPorts.SelectedItem.ToString(), 9600);
            serialPort.DataReceived += SerialPort_DataReceived;
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string data = serialPort.ReadLine();
            this.BeginInvoke(new Action(() => ProcessSerialData(data)));
        }
        private void ReceiveMaxDeformationCallback(IAsyncResult ar)
{
    try
    {
        IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, UdpMaxDeformationPort);
        byte[] data = udpMaxDeformationClient.EndReceive(ar, ref endPoint);
        string message = Encoding.ASCII.GetString(data);

        Console.WriteLine($"Received message on Max Deformation port: {message}");

        if (message.StartsWith("MAX_DEFORMATION:"))
        {
            if (float.TryParse(message.Substring(16), out float deformationValue))
            {
                Console.WriteLine($"Parsed Max Deformation value: {deformationValue}");
                maxDeformation = deformationValue;

                // Update the label on the UI thread
                this.BeginInvoke(new Action(() =>
                {
                    labelMaxDeformation.Text = $"Max Deformation: {maxDeformation:F4}";
                }));

               
            }
            else
            {
                Console.WriteLine($"Failed to parse Max Deformation from message: {message}");
            }
        }
        else
        {
            Console.WriteLine($"Received unexpected message format: {message}");
        }

        // Begin receiving the next message
        udpMaxDeformationClient.BeginReceive(ReceiveMaxDeformationCallback, null);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"UDP Max Deformation Receive error: {ex.Message}");
    }
}



        private void ProcessSerialData(string data)
        {
            if (data.StartsWith("PRESSURE:"))
            {
                if (int.TryParse(data.Substring(9), out int pressureValue))
                {
                    UpdatePressureAndFingerDistanceUI(pressureValue);
                }
            }
        }

        private void UpdatePressureAndFingerDistanceUI(int pressure)
        {
            trackBarPressure.Value = pressure;
            labelPressure.Text = $"Pressure: {pressure}";

            float materialConstant = comboBoxMaterialConstant.SelectedIndex;
            float normalizedMaterialConstant = materialConstant / 3f;
            int maxDistance = trackBarFingerDistance.Maximum;
            int minDistance = trackBarFingerDistance.Minimum;
            float pressureRatio = (float)pressure / trackBarPressure.Maximum;
            float compressionFactor = (float)Math.Pow(normalizedMaterialConstant, 2);
            float adjustedPressureEffect = pressureRatio * compressionFactor;

            int fingerDistance = maxDistance - (int)(adjustedPressureEffect * (maxDistance - minDistance));
            fingerDistance = Math.Max(minDistance, Math.Min(maxDistance, fingerDistance));

            trackBarFingerDistance.Value = fingerDistance;
            labelFingerDistance.Text = $"Finger Distance: {fingerDistance}";

            SendPressureDataToUnity(pressure);
            SendFingerDistanceDataToUnity(fingerDistance);
        }

        private void SendVibrationTick(int intensity)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                string vibrationCommand = $"VIBRATION:{intensity}\n";
                serialPort.Write(vibrationCommand);
            }
        }

        private void UpdateIntensityUI(int intensity)
        {
            if (trackBarIntensity != null)
            {
                trackBarIntensity.Value = Math.Min(trackBarIntensity.Maximum, Math.Max(trackBarIntensity.Minimum, intensity));
                trackBarIntensity.Refresh();
            }
            else
            {
                Console.WriteLine("trackBarIntensity was null in UpdateIntensityUI.");
            }
        }

        private void buttonConnect_Click(object sender, EventArgs e)
        {
            if (!isConnected)
            {
                if (comboBoxComPorts.SelectedItem != null)
                {
                    try
                    {
                        InitializeSerialPort();
                        serialPort.Open();
                        isConnected = true;
                        buttonConnect.Text = "Disconnect";
                        UpdateConnectionStatus("ON");

                        // Initialize material constant only once
                        InitializeMaterialConstant();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error connecting to COM port: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateConnectionStatus("ERROR");
                    }
                }
                else
                {
                    MessageBox.Show("Please select a COM port.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                try
                {
                    if (serialPort != null && serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                    isConnected = false;
                    buttonConnect.Text = "Connect";
                    UpdateConnectionStatus("OFF");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error disconnecting: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }


        private void trackBarPressure_Scroll(object sender, EventArgs e)
        {
            UpdatePressureAndFingerDistanceUI(trackBarPressure.Value);
        }

        private void UpdateConnectionStatus(string status)
        {
            labelStatus.Text = $"Status: {status}";
            labelStatus.ForeColor = status switch
            {
                "ON" => Color.Green,
                "OFF" => Color.Black,
                "ERROR" => Color.Red,
                _ => labelStatus.ForeColor
            };
        }

        private void comboBoxMaterialConstant_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Prevent processing if the change is occurring during initialization
            if (!isConnected) return;

            try
            {
                int selectedIndex = comboBoxMaterialConstant.SelectedIndex;

                // Validate the index
                if (selectedIndex <= 0)
                {
                    comboBoxMaterialConstant.SelectedIndex = 1;
                    return;
                }

                int newConstant = selectedIndex;
                Console.WriteLine($"Material constant changing to: {newConstant}");

                StopPlayback();
                ResetVibrationState();
                UpdatePressureAndFingerDistanceUI(trackBarPressure.Value);

                // Only start playback if we have a valid index
                if (selectedIndex > 0 && selectedIndex <= 3)
                {
                    StartPlayback(selectedIndex - 1);
                    SendMaterialConstantToUnity(newConstant);
                }

                Console.WriteLine($"Material constant change completed. Current state: {newConstant}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in material constant change: {ex.Message}");
            }
        }






        private void ResetVibrationState()
        {
            currentVibrationIntensity = 0f;
            lastForce = 0;
            isPlaying = false;

            if (trackBarPressure != null)
            {
                trackBarPressure.Value = 0;  // Reset the pressure track bar
            }
            else
            {
                Console.WriteLine("trackBarPressure was null.");
            }

            if (trackBarIntensity != null)
            {
                trackBarIntensity.Value = 0;  // Reset the intensity track bar
            }
            else
            {
                Console.WriteLine("trackBarIntensity was null.");
            }

            if (outputDevice != null)
            {
                outputDevice.Stop();
            }
            else
            {
                Console.WriteLine("outputDevice was null.");
            }

            for (int i = 0; i < 3; i++)
            {
                if (audioFileReaders[i] != null)
                {
                    audioFileReaders[i].Position = 0;  // Reset the position of audio file readers
                }
                if (volumeStreams[i] != null)
                {
                    volumeStreams[i].Volume = 0f;  // Reset the volume of volume streams
                }
            }

            UpdateIntensityUI(0);  // Ensure UI reflects the reset state

            Console.WriteLine("Vibration state reset completed.");
        }



        private void TestAudioButton_Click(object sender, EventArgs e)
        {
            try
            {
                Console.WriteLine("Testing audio playback...");
                StartPlayback(0); // Test with the first track
                volumeStreams[0].Volume = 1.0f;
                System.Threading.Thread.Sleep(2000); // Play for 2 seconds
                volumeStreams[0].Volume = 0.0f;
                StopPlayback();
                Console.WriteLine("Audio test completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during audio test: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (serialPort != null && serialPort.IsOpen)
                serialPort.Close();
            if (udpClient != null)
                udpClient.Close();
            updateTimer?.Stop();
            vibrationTimer?.Stop();
            DisposeAudioDevices();
        }

        private void SendPressureDataToUnity(float pressure)
        {
            if (udpClient != null)
            {
                try
                {
                    string message = $"PRESSURE:{pressure}";
                    byte[] data = Encoding.ASCII.GetBytes(message);
                    udpClient.Send(data, data.Length, "127.0.0.1", UdpSendPort);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending UDP data: {ex.Message}");
                }
            }
        }
        private float GetPressureScalingFactor()
        {
            float maxDeformationValue = maxDeformation;
            float scalingFactor;

            if (maxDeformationValue <= 0.4f)
            {
                // Hard objects (0.1 - 0.4): minimal scaling
                scalingFactor = 0.4f;
            }
            else
            {
                // Soft objects (0.5 - 0.9): linear increase in scaling
                scalingFactor = 0.4f + ((maxDeformationValue - 0.4f) * 2.0f);
            }

            return scalingFactor;
        }


        private void SendFingerDistanceDataToUnity(int distance)
        {
            if (udpClient != null)
            {
                try
                {
                    string message = $"FINGER_DISTANCE:{distance}";
                    byte[] data = Encoding.ASCII.GetBytes(message);
                    udpClient.Send(data, data.Length, "127.0.0.1", UdpSendPort);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending UDP data: {ex.Message}");
                }
            }
        }

    }
}