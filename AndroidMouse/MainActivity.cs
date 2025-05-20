using Android.App;
using Android.OS;
using Android.Widget;
using Android.Bluetooth;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace AndroidMouse
{
    [Activity(Label = "AndroidMouse", MainLauncher = true)]
    public class MainActivity : Activity
    {
        private ToggleButton toggleButton;
        private BluetoothAdapter bluetoothAdapter;
        private Button connectButton;
        private BluetoothSocket connectedSocket;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_main);

            toggleButton = FindViewById<ToggleButton>(Resource.Id.toggleButton);
            bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
            connectButton = FindViewById<Button>(Resource.Id.connectButton);

            toggleButton.CheckedChange += async (s, e) =>
            {
                if (e.IsChecked)
                {
                    if (bluetoothAdapter == null || !bluetoothAdapter.IsEnabled)
                    {
                        Toast.MakeText(this, "Enable Bluetooth first", ToastLength.Short).Show();
                        toggleButton.Checked = false;
                        return;
                    }

                    await StartMouseJiggle();
                }
                else
                {
                    StopMouseJiggle();
                }
            };

            connectButton.Click += (s, e) =>
            {
                if (bluetoothAdapter == null || !bluetoothAdapter.IsEnabled)
                {
                    Toast.MakeText(this, "Enable Bluetooth first", ToastLength.Short).Show();
                    return;
                }

                ConnectToComputer();
            };
        }

        private async Task StartMouseJiggle()
        {
            if (connectedSocket == null || !connectedSocket.IsConnected)
            {
                Toast.MakeText(this, "Not connected to computer", ToastLength.Short).Show();
                toggleButton.Checked = false;
                return;
            }

            Toast.MakeText(this, "Mouse jiggle started", ToastLength.Short).Show();
            var outputStream = connectedSocket.OutputStream;

            while (toggleButton.Checked)
            {
                try
                {
                    // Send mouse movement commands (example: "MOVE 10 10")
                    var command = "MOVE 10 10\n";
                    var buffer = System.Text.Encoding.ASCII.GetBytes(command);
                    await outputStream.WriteAsync(buffer, 0, buffer.Length);

                    await Task.Delay(1000); // Simulate delay
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"Error: {ex.Message}", ToastLength.Short).Show();
                    break;
                }
            }
        }

        private void StopMouseJiggle()
        {
            Toast.MakeText(this, "Mouse jiggle stopped", ToastLength.Short).Show();
            // Optionally close the socket or stop communication
        }

        private void ConnectToComputer()
        {
            if (bluetoothAdapter.BondedDevices.Count == 0)
            {
                Toast.MakeText(this, "No paired devices found", ToastLength.Short).Show();
                return;
            }

            var device = bluetoothAdapter.BondedDevices.FirstOrDefault(d => d.Name.Contains("YourComputerName")); // Replace with your computer's Bluetooth name
            if (device == null)
            {
                Toast.MakeText(this, "Computer not found in paired devices", ToastLength.Short).Show();
                return;
            }

            try
            {
                connectedSocket = device.CreateRfcommSocketToServiceRecord(Java.Util.UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")); // Standard UUID for SPP
                connectedSocket.Connect();
                Toast.MakeText(this, "Connected to computer", ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"Connection failed: {ex.Message}", ToastLength.Short).Show();
            }
        }
    }
}