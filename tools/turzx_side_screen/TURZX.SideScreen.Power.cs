using System;

namespace TURZX.SideScreen
{
    internal static class SideScreenPowerApp
    {
        private static int Main(string[] args)
        {
            string port = "COM7";
            string rjcpDllPath = null;
            int brightness = 170;
            bool dryRun = false;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--port") port = Next(args, ref i, arg);
                    else if (arg == "--brightness") brightness = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--rjcp-dll") rjcpDllPath = Next(args, ref i, arg);
                    else if (arg == "--dry-run") dryRun = true;
                    else if (arg == "--help" || arg == "-h")
                    {
                        PrintUsage();
                        return 0;
                    }
                    else
                    {
                        throw new ArgumentException("Unknown argument: " + arg);
                    }
                }

                if (brightness < byte.MinValue || brightness > byte.MaxValue)
                {
                    throw new ArgumentOutOfRangeException("brightness", "Brightness must be between 0 and 255.");
                }

                byte value = (byte)brightness;
                if (dryRun)
                {
                    byte[] packet = TurzxSideScreenProtocol.BuildBrightnessPacket(value);
                    Console.WriteLine(
                        "DRY-RUN command=" + packet[0] +
                        " declaredLength=" + packet[6] +
                        " brightness=" + packet[10] +
                        " packetBytes=" + packet.Length);
                    return 0;
                }

                TurzxSideScreenProtocol.SendBrightness(port, value, rjcpDllPath);
                Console.WriteLine("OK command=123 brightness=" + value + " port=" + port);
                return 0;
            }
            catch (Exception ex)
            {
                Exception detail = ex;
                while (detail.InnerException != null)
                {
                    detail = detail.InnerException;
                }

                Console.Error.WriteLine(detail.GetType().Name + ": " + detail.Message);
                return 1;
            }
        }

        private static string Next(string[] args, ref int index, string argument)
        {
            index++;
            if (index >= args.Length)
            {
                throw new ArgumentException("Missing value for " + argument);
            }

            return args[index];
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "TURZX.SideScreen.Power --port COM7 --brightness 0..255 " +
                "--rjcp-dll <path> [--dry-run]");
        }
    }
}
