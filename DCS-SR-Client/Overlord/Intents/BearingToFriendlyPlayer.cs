﻿using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.LuisModels;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NewRelic.Api.Agent;
using System;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechRecognition;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.GameState;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Intents
{
    class BearingToFriendlyPlayer
    {
        [Trace]
        public static async Task<string> Process(LuisResponse luisResponse, Overlord.GameState.Player sender)
        {
            string response;
            string group = null;
            int flight = -1;
            int element = -1;

            var target = luisResponse.CompositeEntities.Find(x => x.ParentType == "player_callsign");

            if (target != null)
            {
                target.Children.ForEach(x =>
                {
                    if (x["type"] == "learned_group" || x["type"] == "defined_group")
                    {
                        group = x["value"];
                    }
                    else if (x["type"] == "awacs_callsign")
                    {
                        // No-op
                    }
                    else if (x["role"] == "flight_and_element")
                    {
                        Int32.TryParse(x["value"][0].ToString(), out flight);
                        Int32.TryParse(x["value"][1].ToString(), out element);
                    }
                    else if (x["role"] == "flight")
                    {
                        int value = SenderExtractor.mapToInt(x["value"]);
                        if (value == -1)
                        {
                            Int32.TryParse(x["value"], out flight);
                        }
                        else
                        {
                            flight = value;
                        }
                    }
                    else if (x["role"] == "element")
                    {
                        int value = SenderExtractor.mapToInt(x["value"]);

                        if (value == -1)
                        {
                            Int32.TryParse(x["value"], out element);
                        }
                        else
                        {
                            element = value;
                        }
                    }
                });
            }

            if (group == null || flight == -1 || element == -1)
            {
                return "I could not understand the friendly's callsign";
            }
            else
            {
                Contact contact = await GameQuerier.GetFriendlyPlayer(sender.Position, sender.Group, sender.Flight, sender.Plane, group, flight, element);

                if (contact != null)
                {

                    string bearing = Regex.Replace(Util.Geospatial.TrueToMagnetic(sender.Position, contact.Bearing).ToString("000"), "\\d{1}", " $0");
                    string range = contact.Range.ToString();
                    int altitude = (int)contact.Altitude;
                    int angels;
                    if(altitude < 1000)
                    {
                        angels = 1;
                    } else
                    {
                        angels = (altitude % 1000 >= 500 ? altitude + 1000 - altitude % 1000 : altitude - altitude % 1000) / 1000;
                    }

                    response = $"Bra, {bearing}, {range}, angels {angels}";
                }
                else
                {
                    response = $"I cannot find {""} {0} {0}";
                }
            }

            return response;
        }
    }
}