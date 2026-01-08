using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace BluetoothPoC;

class Program
{
    // HFP Hands-Free role UUID: 0000111e-0000-1000-8000-00805f9b34fb
    // This allows the PC to act as a Headset/Hands-Free unit.
    static readonly Guid HfpHandsFreeUuid = Guid.Parse("0000111e-0000-1000-8000-00805f9b34fb");

    static async Task Main(string[] args)
    {
        Console.WriteLine("Bluetooth HFP Hands-Free Unit PoC");
        Console.WriteLine("=================================");

        try
        {
            // 1. Request Access (Required for recent Windows versions)
            // Note: This might require a UI prompt or App Manifest capabilities in a full app.
            // For a console app, it might behave differently or fail if not packaged, 
            // but let's try the API.
            
            // 2. Create the RFCOMM Service Provider
            // This tells Windows we want to host a service with this UUID.
            Console.WriteLine("Creating RFCOMM Service Provider...");
            var serviceId = RfcommServiceId.FromUuid(HfpHandsFreeUuid);
            var provider = await RfcommServiceProvider.CreateAsync(serviceId);

            if (provider == null)
            {
                Console.WriteLine("ERROR: Access denied or hardware not supported. Ensure Bluetooth is on.");
                return;
            }

            // 3. Create a StreamSocketListener to accept connections
            var listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnectionReceived;

            // 4. Start Advertising
            // We need to bind the listener to the service provider's ID.
            await listener.BindServiceNameAsync(provider.ServiceId.AsString(), SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // Configure SDP attributes (Service Discovery Protocol)
            // This is critical so the phone sees us as a "Hands-Free Unit".
            // We typically need to set the Service Name and some HFP specific attributes.
            var writer = new DataWriter();
            
            // Set Service Name
            writer.WriteByte(0x25); // String
            writer.WriteByte((byte)"Rotary Phone HFP".Length);
            writer.WriteString("Rotary Phone HFP");
            
            // Attribute ID 0x0100 is Service Name (simplified for PoC)
            // Note: A full HFP implementation needs complex SDP records (Network features, supported features).
            // provider.SdpRawAttributes.Add(0x0100, writer.DetachBuffer());

            provider.StartAdvertising(listener);

            Console.WriteLine("Listening for connections... (Will exit in 5 seconds for test)");
            Console.WriteLine($"Advertising Service: {HfpHandsFreeUuid}");
            Console.WriteLine("Make your PC discoverable and try to pair from your phone.");
            
            await Task.Delay(5000);
            
            Console.WriteLine("Test completed. Shutting down.");
            provider.StopAdvertising();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CRITICAL ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine("---------------------------------------------------------------");
            Console.WriteLine("NOTE: Using 'Windows.Devices.Bluetooth' in a raw Console App often requires");
            Console.WriteLine("the app to be packaged (MSIX) or run with specific identity capabilities");
            Console.WriteLine("to access Bluetooth radios.");
        }
    }

    private static void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        Console.WriteLine($"Connection received from {args.Socket.Information.RemoteAddress.DisplayName}!");
        
        try
        {
            // In a real implementation, we would start the HFP state machine here.
            // Reading from the socket (AT commands).
            using var reader = new DataReader(args.Socket.InputStream);
            using var writer = new DataWriter(args.Socket.OutputStream);
            
            Console.WriteLine("Socket connected. Ready for AT commands.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
        }
    }
}
