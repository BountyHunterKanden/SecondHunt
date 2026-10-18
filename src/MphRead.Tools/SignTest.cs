using System;
using System.Security.Cryptography;
using MphRecomp.Online;

namespace MphRead
{
    // -signtest [server=wss://...]: the managed P-256 login signature (the fallback for Wine, whose ncrypt can't import
    // an ECDSA private key: 0x80090029). Managed signatures must verify with the platform's ECDSA (what the server runs),
    // then a login to the live server signed only by the managed code must be welcomed.
    internal static class SignTest
    {
        public static void Run(string[] args)
        {
            string server = "wss://play.secondhuntonline.com/v1/ws";
            foreach (string arg in args)
            {
                if (arg.StartsWith("server=", StringComparison.Ordinal)) server = arg["server=".Length..];
            }
            int failed = 0;
            OnlineIdentity.ManagedSign = true;
            for (int i = 0; i < 200; i++)
            {
                OnlineIdentity id = OnlineIdentity.Create();
                byte[] data = RandomNumberGenerator.GetBytes(1 + i);
                byte[] sig = id.Sign(data);
                if (sig.Length != 64 || !OnlineIdentity.Verify(id.PublicKey, data, sig))
                {
                    failed++;
                    Console.WriteLine($"FAIL managed signature {i} doesn't verify");
                }
                data[0] ^= 1;
                if (OnlineIdentity.Verify(id.PublicKey, data, sig))
                {
                    failed++;
                    Console.WriteLine($"FAIL managed signature {i} verifies changed data");
                }
            }
            Console.WriteLine($"managed signatures: 200 checked against the platform verifier, {failed} failed");
            try
            {
                OnlineClient client = OnlineClient.ConnectAsync(new Uri(server), OnlineIdentity.Create(), "SIGNTEST", "signtest")
                    .GetAwaiter().GetResult();
                Console.WriteLine($"live login with the managed signature: welcomed by {server} (server {client.ServerVersion})");
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL live login: {ex.Message}");
            }
            Console.WriteLine(failed == 0 ? "-signtest PASS" : $"-signtest FAIL ({failed})");
        }
    }
}
