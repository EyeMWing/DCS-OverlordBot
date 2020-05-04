﻿using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Navigation;
using Newtonsoft.Json;
using NLog;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.GameState;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Atc
{

    class Manager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        public static readonly List<Airfield> Airfields = PopulateAirfields();

        public Manager()
        {
            Logger.Debug("Starting ATC Manager");
            _ = Discord.DiscordClient.SendToAtcLogChannel("ATC Manager restarted");
            Task.Run(() => CheckNavigationPointsAsync());
        }

        static private List<Airfield> PopulateAirfields()
        {
            List<Airfield> airfields = new List<Airfield>();

            string[] fileArray = Directory.GetFiles("Overlord/Data/Airfields/", "*.json");

            foreach (string file in fileArray)
            {
                airfields.Add(JsonConvert.DeserializeObject<Airfield>(File.ReadAllText(file)));
            }

            return airfields;
        }

        private async Task CheckNavigationPointsAsync()
        {
            while (true)
            {
                Thread.Sleep(2000);
                Logger.Trace($"Checking Airfields");

                bool positionLogged = false;
                foreach (var airfield in Airfields)
                {
                    Logger.Trace($"Checking {airfield.Name}");

                    List<GameObject> neabyAircraft = await GameQuerier.GetAircraftNearAirfield(airfield);

                    if(neabyAircraft.Count == 0)
                    {
                        Logger.Trace($"No Aircraft within 10nm of {airfield.Name}");
                    }

                    foreach (var aircraft in neabyAircraft)
                    {
                        if (!airfield.Aircraft.ContainsKey(aircraft.Id))
                        {
                            AircraftState.State state;

                            if (aircraft.Altitude > airfield.Altitude + 30)
                            {
                                state = AircraftState.State.Flying;
                            }
                            else
                            {
                                state = AircraftState.State.OnGround;
                            }
                            airfield.Aircraft.Add(aircraft.Id, new AircraftState(airfield, aircraft, state));
                            positionLogged = true;
                        }
                        if (positionLogged == true) { continue; }


                        var stateMessage = $"Aircraft ID: {aircraft.Id} (Pilot {aircraft.Pilot}) is at state: {airfield.Aircraft[aircraft.Id].CurrentState}";
                        Logger.Debug(stateMessage);

                        if (aircraft.Altitude > airfield.Altitude + 10) { 
                            if (airfield.Aircraft[aircraft.Id].CurrentState == AircraftState.State.OnRunway)
                            {
                                airfield.Aircraft[aircraft.Id].Takeoff();
                                positionLogged = true;
                            }
                        }
                        if (positionLogged == true) { continue; }


                        foreach (var runway in airfield.Runways)
                        {
                            if (runway.Area.GetBounds().Contains(aircraft.Position))
                            {
                                if (aircraft.Altitude <= airfield.Altitude + 10 && airfield.Aircraft[aircraft.Id].CurrentState != AircraftState.State.Outbound)
                                {
                                    airfield.Aircraft[aircraft.Id].EnterRunway(runway);
                                }
                                else if (aircraft.Altitude >= airfield.Altitude + 5 && airfield.Aircraft[aircraft.Id].CurrentState == AircraftState.State.Outbound)
                                {
                                    // No-op to stop flicking between take-off and landing.
                                }
                                else 
                                {
                                    airfield.Aircraft[aircraft.Id].Land(runway);
                                }
                                positionLogged = true;
                                break;
                            }
                        }
                        if(positionLogged == true) { continue; }

                        foreach (var navigationPoint in airfield.TaxiwayPoints)
                        {
                            if (navigationPoint.Position.GetBounds().Contains(aircraft.Position))
                            {
                                if (airfield.Aircraft[aircraft.Id].CurrentState == AircraftState.State.OnRunway)
                                {
                                    airfield.Aircraft[aircraft.Id].EnterBoundaryFromRunway(navigationPoint);
                                } else if (airfield.Aircraft[aircraft.Id].CurrentState == AircraftState.State.OnGround)
                                {
                                    airfield.Aircraft[aircraft.Id].EnterBoundaryFromTaxiway(navigationPoint);
                                } else
                                {
                                    Logger.Error($"Could not determine how {aircraft.Id} entered {navigationPoint.Name}");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

}
