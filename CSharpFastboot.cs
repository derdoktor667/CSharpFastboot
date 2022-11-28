
using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
namespace CSharpFastboot
{
    // ...methods to communicate with fastboot devices
    // ...methods: wait, connect, disconnect, get serial number, execute command, upload data, get devices
    public class CSharpFastboot
    {
        // ...fastboot device identifier
        private const int USB_VID = 0x18D1;
        private const int USB_PID = 0xD00D;
        private const int HEADER_SIZE = 4;
        private const int BLOCK_SIZE = 512 * 1024; // 512 KB

        // ...strings for output
        private const string NO_DEVICE_FOUND = "No fastboot devices found!";
        private const string TIMEOUT_ERROR = "Fastboot Timeout Error!";

        public int Timeout { get; set; } = 3000;

        private UsbDevice device;
        private string targetSerialNumber;

        public enum Status
        {
            Fail,
            Okay,
            Data,
            Info,
            Unknown
        }

        public class Response
        {
            public Status Status { get; set; }
            public string Payload { get; set; }
            public byte[] RawData { get; set; }

            public Response(Status status, string payload)
            {
                Status = status;
                Payload = payload;
            }
        }

        public CSharpFastboot(string serial)
        {
            targetSerialNumber = serial;
        }

        public CSharpFastboot()
        {
            targetSerialNumber = null;
        }

        private Status GetStatusFromString(string header)
        {
            switch (header)
            {
                case "INFO":
                    return Status.Info;
                case "OKAY":
                    return Status.Okay;
                case "DATA":
                    return Status.Data;
                case "FAIL":
                    return Status.Fail;
                default:
                    return Status.Unknown;
            }
        }

        // ...wait for device.
        public void Wait()
        {
            var counter = 0;

            while (true)
            {
                var allDevices = UsbDevice.AllDevices;

                if (allDevices.Any(x => x.Vid == USB_VID & x.Pid == USB_PID))
                {
                    return;
                }

                if (counter == 50)
                {
                    throw new Exception(TIMEOUT_ERROR);
                }

                Thread.Sleep(500);
                counter++;
            }
        }

        // ...connect to first detected fastboot device.
        public void Connect()
        {
            UsbDeviceFinder finder;

            if (string.IsNullOrWhiteSpace(targetSerialNumber))
            {
                finder = new UsbDeviceFinder(USB_VID, USB_PID);
            }
            else
            {
                finder = new UsbDeviceFinder(USB_VID, USB_PID, targetSerialNumber);
            }

            device = UsbDevice.OpenUsbDevice(finder);

            if (device == null)
            {
                throw new Exception(NO_DEVICE_FOUND);
            }

            var wDev = device as IUsbDevice;

            if (wDev is IUsbDevice)
            {
                wDev.SetConfiguration(1);
                wDev.ClaimInterface(0);
            }
        }

        // ...disconnect from device.
        public void Disconnect()
        {
            device.Close();
        }

        // ...get device serial number.
        public string GetSerialNumber()
        {
            return device.Info.SerialString;
        }

        //  ...send command to device and read response.
        public Response Command(byte[] command)
        {
            var writeEndpoint = device.OpenEndpointWriter(WriteEndpointID.Ep01);
            var readEndpoint = device.OpenEndpointReader(ReadEndpointID.Ep01);

            writeEndpoint.Write(command, Timeout, out int wrAct);

            if (wrAct != command.Length)
            {
                throw new Exception($"Failed to write command! Transfered: {wrAct} of {command.Length} bytes");
            }

            Status status;
            var response = new StringBuilder();
            var buffer = new byte[64];
            string strBuffer;

            while (true)
            {
                readEndpoint.Read(buffer, Timeout, out int rdAct);

                strBuffer = Encoding.ASCII.GetString(buffer);

                if (strBuffer.Length < HEADER_SIZE)
                {
                    status = Status.Unknown;
                }
                else
                {
                    var header = new string(strBuffer
                        .Take(HEADER_SIZE)
                        .ToArray());

                    status = GetStatusFromString(header);
                }

                response.Append(strBuffer
                    .Skip(HEADER_SIZE)
                    .Take(rdAct - HEADER_SIZE)
                    .ToArray());

                response.Append("\n");

                if (status != Status.Info)
                {
                    break;
                }
            }

            var str = response
                .ToString()
                .Replace("\r", string.Empty)
                .Replace("\0", string.Empty);

            return new Response(status, str)
            {
                RawData = Encoding.ASCII.GetBytes(strBuffer)
            };
        }

        // ...send data command to device.
        private void SendDataCommand(long size)
        {
            if (Command($"download:{size:X8}").Status != Status.Data)
            {
                throw new Exception($"Invalid response from device! (data size: {size})");
            }
        }

        // ...transfer block to device from stream to USB write endpoint with fixed size
        private void TransferBlock(FileStream stream, UsbEndpointWriter writeEndpoint, byte[] buffer, int size)
        {
            stream.Read(buffer, 0, size);
            writeEndpoint.Write(buffer, Timeout, out int act);

            if (act != size)
            {
                throw new Exception($"Failed to transfer block (sent {act} of {size})");
            }
        }

        // ...upload data from fileStream to device buffer
        public void UploadData(FileStream stream)
        {
            var writeEndpoint = device.OpenEndpointWriter(WriteEndpointID.Ep01);
            var readEndpoint = device.OpenEndpointReader(ReadEndpointID.Ep01);

            var length = stream.Length;
            var buffer = new byte[BLOCK_SIZE];

            SendDataCommand(length);

            while (length >= BLOCK_SIZE)
            {
                TransferBlock(stream, writeEndpoint, buffer, BLOCK_SIZE);
                length -= BLOCK_SIZE;
            }

            if (length > 0)
            {
                buffer = new byte[length];
                TransferBlock(stream, writeEndpoint, buffer, (int)length);
            }

            var resBuffer = new byte[64];

            readEndpoint.Read(resBuffer, Timeout, out _);

            var strBuffer = Encoding.ASCII.GetString(resBuffer);

            if (strBuffer.Length < HEADER_SIZE)
            {
                throw new Exception($"Invalid response from device: {strBuffer}");
            }

            var header = new string(strBuffer
                .Take(HEADER_SIZE)
                .ToArray());

            var status = GetStatusFromString(header);

            if (status != Status.Okay)
            {
                throw new Exception($"Invalid status: {strBuffer}");
            }
        }

        // ...upload data from file to device buffer
        public void UploadData(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open))
            {
                UploadData(stream);
            }
        }

        // ...list available devices and returns an array
        public static string[] GetDevices()
        {
            UsbDevice dev;

            var devices = new List<string>();

            var allDevices = UsbDevice.AllDevices;

            foreach (UsbRegistry usbRegistry in allDevices)
            {
                if (usbRegistry.Vid != USB_VID || usbRegistry.Pid != USB_PID)
                {
                    continue;
                }

                if (usbRegistry.Open(out dev))
                {
                    devices.Add(dev.Info.SerialString);
                    dev.Close();
                }
            }

            return devices.ToArray();
        }

        // ...send command to device and read response.
        public Response Command(string command)
        {
            return Command(Encoding.ASCII.GetBytes(command));
        }
    }
}
