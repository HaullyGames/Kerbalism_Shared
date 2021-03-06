﻿using System;
using System.Collections.Generic;
using KSP.Localization;
using CommNet;

namespace KERBALISM
{
	/// <summary> signal connection link status </summary>
	public enum LinkStatus
	{
		direct_link,
		indirect_link,	// relayed signal
		no_link,
		plasma,			// plasma blackout on reentry
		storm			// cme storm blackout
	};

	/// <summary> Stores a single vessels communication info</summary>
	public sealed class ConnectionInfo
	{
		/// <summary> true if there is a connection back to DSN </summary>
		public bool linked = false;

		/// <summary> status of the connection </summary>
		public LinkStatus status = LinkStatus.no_link;

		/// <summary> Controller Path </summary>
		public Guid[] controlPath;

		/// <summary> science data rate. note that internal transmitters can not transmit science data only telemetry data </summary>
		public double rate = 0.0;

		/// <summary> internal transmitter ec cost (control and telemetry) </summary>
		public double internal_cost = 0.0;

		/// <summary> external transmitter ec cost </summary>
		public double external_cost = 0.0;

		/// <summary> signal strength, or when using RemoteTech signal delay </summary>
		public double strength = 0.0;

		/// <summary> receiving node name </summary>
		public string target_name = "";

		// constructor
		/// <summary> Creates a <see cref="ConnectionInfo"/> object for the specified vessel from it's antenna modules</summary>
		public ConnectionInfo(Vessel v, bool powered, bool storm)
		{
			// set RemoteTech powered and storm state
			if (RemoteTech.Enabled)
			{
				RemoteTech.SetPoweredDown(v.id, !powered);
				RemoteTech.SetCommsBlackout(v.id, storm);
			}

			// return no connection if there is no ec left
			if (!powered)
			{
				// hysteresis delay
				if ((DB.Vessel(v).hyspos_signal >= 5.0))
				{
					DB.Vessel(v).hyspos_signal = 5.0;
					DB.Vessel(v).hysneg_signal = 0.0;
					return;
				}
				DB.Vessel(v).hyspos_signal += 0.1;
			}
			else
			{
				// hysteresis delay
				DB.Vessel(v).hysneg_signal += 0.1;
				if (!(DB.Vessel(v).hysneg_signal >= 5.0))
					return;
				DB.Vessel(v).hysneg_signal = 5.0;
				DB.Vessel(v).hyspos_signal = 0.0;
			}

			rate = 0.0;
			internal_cost = 0.0;
			external_cost = 0.0;

			// CommNet or simple signal system
			if (!RemoteTech.Enabled)
			{
				List<ModuleDataTransmitter> transmitters;

				// if vessel is loaded
				if (v.loaded)
				{
					// find transmitters
					transmitters = v.FindPartModulesImplementing<ModuleDataTransmitter>();

					if (transmitters != null)
					{
						foreach (ModuleDataTransmitter t in transmitters)
						{
							if (t.antennaType == AntennaType.INTERNAL) // do not include internal data rate, ec cost only
								internal_cost += t.DataResourceCost * t.DataRate;
							else
							{
								// do we have an animation
								ModuleDeployableAntenna animation = t.part.FindModuleImplementing<ModuleDeployableAntenna>();
								ModuleAnimateGeneric animationGeneric = t.part.FindModuleImplementing<ModuleAnimateGeneric>();
								if (animation != null)
								{
									// only include data rate and ec cost if transmitter is extended
									if (animation.deployState == ModuleDeployablePart.DeployState.EXTENDED)
									{
										rate += t.DataRate;
										external_cost += t.DataResourceCost * t.DataRate;
									}
								}
								else if (animationGeneric != null)
								{
									// only include data rate and ec cost if transmitter is extended
									if (animationGeneric.animSpeed > 0)
									{
										rate += t.DataRate;
										external_cost += t.DataResourceCost * t.DataRate;
									}
								}
								// no animation
								else
								{
									rate += t.DataRate;
									external_cost += t.DataResourceCost * t.DataRate;
								}
							}
						}
					}
				}

				// if vessel is not loaded
				else
				{
					// find proto transmitters
					foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
					{
						// get part prefab (required for module properties)
						Part part_prefab = PartLoader.getPartInfoByName(p.partName).partPrefab;

						transmitters = part_prefab.FindModulesImplementing<ModuleDataTransmitter>();

						if (transmitters != null)
						{
							foreach (ModuleDataTransmitter t in transmitters)
							{
								if (t.antennaType == AntennaType.INTERNAL) // do not include internal data rate, ec cost only
									internal_cost += t.DataResourceCost * t.DataRate;
								else
								{
									// do we have an animation
									ProtoPartModuleSnapshot m = p.FindModule("ModuleDeployableAntenna") ?? p.FindModule("ModuleAnimateGeneric");
									if (m != null)
									{
										// only include data rate and ec cost if transmitter is extended
										string deployState = Lib.Proto.GetString(m, "deployState");
										float animSpeed = Lib.Proto.GetFloat(m, "animSpeed");
										if (deployState == "EXTENDED" || animSpeed > 0)
										{
											rate += t.DataRate;
											external_cost += t.DataResourceCost * t.DataRate;
										}
									}
									// no animation
									else
									{
										rate += t.DataRate;
										external_cost += t.DataResourceCost * t.DataRate;
									}
								}
							}
						}
					}
				}

				// if CommNet is enabled
				if (HighLogic.fetch.currentGame.Parameters.Difficulty.EnableCommNet)
				{
					if (v.connection != null)
					{
						// force CommNet update of unloaded vessels
						if (!v.loaded)
							Lib.ReflectionValue(v.connection, "unloadedDoOnce", true);

						// are we connected to DSN or control station(can be another vessel with 3 or more crew in CommNet)
						if (v.connection.IsConnected)
						{
							linked = true;
							status = v.connection.ControlPath.First.hopType == HopType.Home ? LinkStatus.direct_link : LinkStatus.indirect_link;
							strength = v.connection.SignalStrength;

							if (status != LinkStatus.direct_link)
							{
								Vessel firstHop = Lib.CommNodeToVessel(v.Connection.ControlPath.First.end);
								// Get rate from the firstHop, each Hop will do the same logic, then we will have the min rate for whole path
								rate = Math.Min(Cache.VesselInfo(FlightGlobals.FindVessel(firstHop.id)).connection.rate, rate);
							}

							rate *= strength * PreferencesBasic.Instance.transmitFactor;
							target_name = Lib.Ellipsis(Localizer.Format(v.connection.ControlPath.First.end.displayName).Replace("Kerbin", "DSN"), 20);
						}

						// is loss of connection due to plasma blackout
						else if (Lib.ReflectionValue<bool>(v.connection, "inPlasma"))  // calling InPlasma causes a StackOverflow :(
						{
							status = LinkStatus.plasma;
							rate = 0.0;
							internal_cost = 0.0;
							external_cost = 0.0;
						}
					}
					// no connection
					else
					{
						rate = 0.0;
						internal_cost = 0.0;
						external_cost = 0.0;
					}
					return;
				}
				// the simple stupid always connected signal system
				linked = true;
				status = LinkStatus.direct_link;
				strength = 1;    // 100 %
				target_name = "DSN: KSC";
				return;
			}

			// RemoteTech signal system
			else
			{
				// if vessel is loaded
				if (v.loaded)
				{
					// find transmitters
					foreach (Part p in v.parts)
					{
						foreach (PartModule m in p.Modules)
						{
							// calculate internal (passive) transmitter ec usage @ 0.5W each
							if (m.moduleName == "ModuleRTAntennaPassive")
								internal_cost += 0.0005;

							// calculate external transmitters
							else if (m.moduleName == "ModuleRTAntenna")
							{
								// only include ec cost if transmitter is active
								if (Lib.ReflectionValue<bool>(m, "IsRTActive"))
								{
									rate += Lib.ReflectionValue<float>(m, "RTPacketSize") / Lib.ReflectionValue<float>(m, "RTPacketInterval");
									external_cost += m.resHandler.inputResources.Find(r => r.name == "ElectricCharge").rate;
								}
							}
						}
					}
				}

				// if vessel is not loaded
				else
				{
					// find proto transmitters
					foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
					{
						// get part prefab (required for module properties)
						Part part_prefab = PartLoader.getPartInfoByName(p.partName).partPrefab;
						int index = 0;      // module index

						foreach (ProtoPartModuleSnapshot m in p.modules)
						{
							// calculate internal (passive) transmitter ec usage @ 0.5W each
							if (m.moduleName == "ModuleRTAntennaPassive")
								internal_cost += 0.0005;

							// calculate external transmitters
							else if (m.moduleName == "ModuleRTAntenna")
							{
								// only include data rate and ec cost if transmitter is active skip if index is out of range
								if (Lib.Proto.GetBool(m, "IsRTActive") && index < part_prefab.Modules.Count)
								{
									// get module prefab
									PartModule pm = part_prefab.Modules.GetModule(index);

									if (pm != null)
									{
										external_cost += pm.resHandler.inputResources.Find(r => r.name == "ElectricCharge").rate;
										// only include data rate if vessel is connected
										float? packet_size = Lib.SafeReflectionValue<float>(pm, "RTPacketSize");
										// workaround for old savegames
										if (packet_size == null)
										{
											Lib.Debug("Old SaveGame PartModule ModuleRTAntenna for part {0} on unloaded vessel {1}, using default values as a workaround", p.partName, v.vesselName);
											rate += 6.6666;  // 6.67 Mb/s
										}
										else
										{
											rate += (float)packet_size / Lib.ReflectionValue<float>(pm, "RTPacketInterval");
										}
									}
									else
									{
										Lib.Debug("Could not find PartModule ModuleRTAntenna for part {0} on unloaded vessel {1}, using default values as a workaround", p.partName, v.vesselName);
										rate += 6.6666;          // 6.67 Mb/s in 100% factor
										external_cost += 0.025;  // 25 W/s
									}
								}
							}
							index++;
						}
					}
				}

				// are we connected
				if (RemoteTech.Connected(v.id))
				{
					linked = RemoteTech.ConnectedToKSC(v.id);
					status = RemoteTech.TargetsKSC(v.id) ? LinkStatus.direct_link : LinkStatus.indirect_link;
					strength = RemoteTech.GetSignalDelay(v.id);
					target_name = status == LinkStatus.direct_link ? Lib.Ellipsis("DSN: " + (RemoteTech.NameTargetsKSC(v.id) ?? ""), 20) :
																	 Lib.Ellipsis(RemoteTech.NameFirstHopToKSC(v.id) ?? "", 20);

					if (linked) controlPath = RemoteTech.GetCommsControlPath(v.id);

					// Get the smaller rate of the path
					if (controlPath != null)
					{
						// Get rate from the firstHop, each Hop will do the same logic, then we will have the min rate for whole path
						if (controlPath.Length > 0)
							rate = Math.Min(Cache.VesselInfo(FlightGlobals.FindVessel(controlPath[0])).connection.rate, rate);
					}
					rate *= PreferencesBasic.Instance.transmitFactor;
				}
				// is loss of connection due to a blackout
				else if (RemoteTech.GetCommsBlackout(v.id))
				{
					status = storm ? LinkStatus.storm : LinkStatus.plasma;
					rate = 0.0;
					internal_cost = 0.0;
					external_cost = 0.0;
				}
				else
				{
					// no connection
					rate = 0.0;
					internal_cost = 0.0;
					external_cost = 0.0;
				}
			}
		}
	}
} // KERBALISM
