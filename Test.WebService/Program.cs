/*
  This file is part of MIG (https://github.com/genielabs/mig-service-dotnet)
 
  Copyright (2012-2026) G-Labs (https://github.com/genielabs)

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

using GLabs.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MIG;

namespace Test.WebService
{
    class MainClass
    {
        public static void Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var serviceProvider = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConfiguration(configuration.GetSection("Logging"));
                    builder.AddConsole();
                })
                .BuildServiceProvider();

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            LogManager.Initialize(loggerFactory);

            string webPort = args.FirstOrDefault(a => a.StartsWith("--port="))?.Split('=')[1] ?? "8088";

            Console.WriteLine("=================================================");
            Console.WriteLine("  MIG.Audio - WebService Gateway Test Host");
            Console.WriteLine("  URL: http://localhost:{0}", webPort);
            Console.WriteLine("=================================================");

            var migService = new MigService();

            // Add and configure the Web gateway
            var web = migService.AddGateway("WebServiceGateway");
            web.SetOption("HomePath", "html");
            web.SetOption("BaseUrl", "/pages/");
            web.SetOption("Host", args.FirstOrDefault(a => a.StartsWith("--host="))?.Substring(7) ?? "localhost");
            web.SetOption("Port", webPort);
            web.SetOption("Password", "");
            web.SetOption("EnableFileCaching", "False");

            if (!migService.StartService()) throw new InvalidOperationException("Cannot start the HTTP gateway.");

            // 1. Enable Speech-to-Text Interface (VoiceRecognizer)
            Console.WriteLine("Loading Audio.VoiceRecognizer...");
            migService.AddInterface("Audio.VoiceRecognizer", "MIG.Audio.dll");
            migService.GetInterface("Audio.VoiceRecognizer").IsEnabled = true;

            // 2. Enable Text-to-Speech Interface (VoiceSynthesizer)
            Console.WriteLine("Loading Audio.VoiceSynthesizer...");
            migService.AddInterface("Audio.VoiceSynthesizer", "MIG.Audio.dll");
            migService.GetInterface("Audio.VoiceSynthesizer").IsEnabled = true;

            Console.WriteLine($"POST http://localhost:{webPort}/api/Audio.VoiceRecognizer/chat-1/Voice.Transcribe/it/base (WAV or PCM body)");
            Console.WriteLine($"POST http://localhost:{webPort}/api/Audio.VoiceSynthesizer/chat-1/Voice.Synthesize/1.15 (UTF-8 text body)");
            Console.WriteLine("Engines load on the first valid audio request. Ctrl+C stops the host.");
            if (args.Contains("--self-test") || args.Contains("--native-test"))
            {
                try { ApiSmokeTests.Run($"http://localhost:{webPort}/", args.Contains("--native-test"), migService).GetAwaiter().GetResult(); }
                finally { migService.StopService(); }
                return;
            }
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; migService.StopService(); Environment.Exit(0); };
            while (true)
            {
                Thread.Sleep(10000);
            }
        }
    }
}



