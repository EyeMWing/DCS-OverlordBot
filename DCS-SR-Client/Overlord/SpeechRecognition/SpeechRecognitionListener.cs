﻿using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Intents;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.LuisModels;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechOutput;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using FragLabs.Audio.Codecs;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using NLog;
using System.IO;
using NewRelic.Api.Agent;
using System.Collections.Concurrent;
using static Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.GameState;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Discord;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechRecognition
{
    public class SpeechRecognitionListener
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Used when an exception is thrown so that the caller isn't left wondering.
        private static readonly byte[] _failureMessage = File.ReadAllBytes("Overlord/equipment-failure.wav");

        private readonly BufferedWaveProviderStreamReader _streamReader;
        private readonly AudioConfig _audioConfig;
        private readonly OpusEncoder _encoder;

        private readonly string _voice;

        public UdpVoiceHandler _voiceHandler;

        private ConcurrentQueue<byte[]> _responses;

        public bool FinishedListening;

        // Allows OverlordBot to listen for a specific word to start listening. Currently not used although the setup has all been done.
        // This is due to wierd state transition errors thatI cannot be bothered to debug.
        KeywordRecognitionModel _wakeWord;

        public SpeechRecognitionListener(BufferedWaveProviderStreamReader streamReader, ConcurrentQueue<byte[]> responseQueue, string callsign = null, string voice = "en-US-JessaRUS")
        {
            Logger.Debug("VOICE: " + voice);

            _voice = voice;

           _encoder = OpusEncoder.Create(AudioManager.INPUT_SAMPLE_RATE, 1, FragLabs.Audio.Codecs.Opus.Application.Voip);
           _encoder.ForwardErrorCorrection = false;
           _encoder.FrameByteCount(AudioManager.SEGMENT_FRAMES);

            _streamReader = streamReader;
            _audioConfig = AudioConfig.FromStreamInput(_streamReader, AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

            _wakeWord = KeywordRecognitionModel.FromFile($"Overlord/WakeWords/{callsign}.table");

            _responses = responseQueue;
        }

        // Gets an authorization token by sending a POST request to the token service.
        public static async Task<string> GetToken()
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", Settings.SPEECH_SUBSCRIPTION_KEY);
                UriBuilder uriBuilder = new UriBuilder("https://" + Settings.SPEECH_REGION + ".api.cognitive.microsoft.com/sts/v1.0/issueToken");

                using (var result = await client.PostAsync(uriBuilder.Uri.AbsoluteUri, null))
                {
                    if (result.IsSuccessStatusCode)
                    {
                        return await result.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        throw new HttpRequestException($"Cannot get token from {uriBuilder.ToString()}. Error: {result.StatusCode}");
                    }
                }
            }
        }

        public async Task StartListeningAsync()
        {
            Logger.Debug($"Started Recognition");

            var authorizationToken = await GetToken();
            SpeechConfig speechConfig = SpeechConfig.FromAuthorizationToken(authorizationToken, Settings.SPEECH_REGION);
            speechConfig.EndpointId = Settings.SPEECH_CUSTOM_ENDPOINT_ID;
            SpeechRecognizer recognizer = new SpeechRecognizer(speechConfig, _audioConfig);

            var result = await recognizer.RecognizeOnceAsync();

            if (result.Reason == ResultReason.RecognizedSpeech)
            {
                _ = ProcessAwacsCall(result);
            }
            else if (result.Reason == ResultReason.NoMatch)
            {
                Logger.Debug($"Speech could not be recognized.");
            }
            else if (result.Reason == ResultReason.Canceled)
            {
                var cancellation = CancellationDetails.FromResult(result);
                Logger.Debug($"CANCELED: Reason={cancellation.Reason}");

                if (cancellation.Reason == CancellationReason.Error)
                {
                    Logger.Debug($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                    Logger.Debug($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                    Logger.Debug($"CANCELED: Did you update the subscription info?");
                }
            }
            Logger.Debug($"Stopped Recognition");
            FinishedListening = true;
        }

        [Transaction(Web = true)]
        private async Task ProcessAwacsCall(SpeechRecognitionResult result) {
            string response = null;

            try
            {
                if (result.Reason == ResultReason.RecognizedSpeech)
                {
                    // Send data to the nextgen shadow system. This is not part of the main flow so we don't await.
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    //LuisServiceV3.RecognizeAsync(e.Result.Text);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

                    Logger.Info($"Incoming Transmission: {result.Text}");
                    string luisJson = Task.Run(() => LuisService.ParseIntent(result.Text)).Result;
                    Logger.Debug($"LIVE LUIS RESPONSE: {luisJson}");
                    LuisResponse luisResponse = JsonConvert.DeserializeObject<LuisResponse>(luisJson);

                    string awacs;
                    Sender sender = Task.Run(() => SenderExtractor.Extract(luisResponse)).Result;

                    if (luisResponse.Query != null && luisResponse.TopScoringIntent["intent"] == "None" ||
                        luisResponse.Entities.Find(x => x.Type == "awacs_callsign") == null)
                    {
                        Logger.Debug($"RESPONSE NO-OP");
                        string transmission = "Transmission Ignored\nIncoming: " + result.Text;
                        await DiscordClient.SendTransmission(transmission).ConfigureAwait(false); ;
                        // NO-OP
                    }
                    else if (sender == null)
                    {
                        Logger.Debug($"SENDER IS NULL");
                        response = "Last transmitter, I could not recognise your call-sign.";
                    }
                    else
                    {
                        awacs = luisResponse.Entities.Find(x => x.Type == "awacs_callsign").Resolution.Values[0];

                        Logger.Debug($"SENDER: " + sender);

                        sender.GameObject = Task.Run(() => GetPilotData(sender.Group, sender.Flight, sender.Plane)).Result;

                        if (sender.GameObject == null)
                        {
                            Logger.Trace($"SenderVerified: false");
                            response = $"{sender}, {awacs}, I cannot find you on scope. ";
                        }
                        else
                        {
                            if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "RadioCheck"))
                            {
                                response = $"{sender}, {awacs}, five by five";
                            }
                            else if (luisResponse.Query != null && luisResponse.TopScoringIntent["intent"] == "BogeyDope")
                            {
                                response = $"{sender}, {awacs}, ";
                                response += Task.Run(() => BogeyDope.Process(sender)).Result;
                            }
                            else if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "BearingToAirbase"))
                            {
                                response = $"{sender}, {awacs}, ";
                                response += Task.Run(() => BearingToAirbase.Process(luisResponse, sender)).Result;
                            }
                            else if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "BearingToFriendlyPlayer"))
                            {
                                response = $"{sender}, {awacs}, ";
                                response += Task.Run(() => BearingToFriendlyPlayer.Process(luisResponse, sender)).Result;
                            }
                            else if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "SetWarningRadius"))
                            {
                                response = $"{sender}, {awacs}, ";
                                response += Task.Run(() => SetWarningRadius.Process(luisResponse, sender, awacs,_voice, _responses)).Result;
                            }
                            else if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "Picture"))
                            {
                                response = $"{sender}, {awacs}, We do not support picture calls ";
                            }
                            else if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "Declare"))
                            {
                                response = $"{sender}, {awacs}, ";
                                response += Task.Run(() => Declare.Process(luisResponse, sender)).Result;
                            }
                        }
                    }
                }
                else if (result.Reason == ResultReason.NoMatch)
                {
                    Logger.Debug($"NOMATCH: Speech could not be recognized.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error processing radio call");
                _responses.Enqueue(_failureMessage);
                response = null;
            }
            if (response != null)
            {
                Logger.Info($"Outgoing Transmission: {response}");
                string transmission = "Transmission pair\nIncoming: " + result.Text + "\nOutgoing: " + response;
                await DiscordClient.SendTransmission(transmission).ConfigureAwait(false);
                var audioResponse = await Task.Run(() => Speaker.CreateResponse($"<speak version=\"1.0\" xmlns=\"https://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\"><voice name =\"{_voice}\">{response}</voice></speak>"));
                _responses.Enqueue(audioResponse);
            }
        }
    }
}
