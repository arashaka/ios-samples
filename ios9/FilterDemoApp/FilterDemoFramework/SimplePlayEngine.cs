﻿using System;
using System.Collections.Generic;
using System.Linq;

using AudioUnit;
using AVFoundation;
using CoreFoundation;
using Foundation;

namespace FilterDemoFramework {
	public class SimplePlayEngine {
		readonly AVAudioPlayerNode player = new AVAudioPlayerNode ();

		DispatchQueue stateChangeQueue = new DispatchQueue ("SimplePlayEngine.stateChangeQueue", false);
		AVAudioEngine engine = new AVAudioEngine ();
		AVAudioUnit effect;
		AVAudioFile file;
		bool isPlaying;

		Action ComponentsFoundCallback;
		DispatchQueue availableEffectsAccessQueue = new DispatchQueue ("SimplePlayEngine.availableEffectsAccessQueue", false);
		List <AVAudioUnitComponent> availableEffects = new List <AVAudioUnitComponent> ();

		public AUAudioUnit AudioUnit { get; private set; }

		public List <AUAudioUnitPreset> PresetList { get; private set; } = new List <AUAudioUnitPreset> ();

		public AVAudioUnitComponent [] AvailableEffects {
			get {
				AVAudioUnitComponent[] result = null;

				availableEffectsAccessQueue.DispatchSync (() => {
					result = availableEffects.ToArray ();
				});

				return result;
			}
			set {
				availableEffectsAccessQueue.DispatchSync (() => {
					availableEffects = value.ToList ();
				});
			}
		}

		public SimplePlayEngine (Action componentsFoundCallback = null)
		{
			ComponentsFoundCallback = componentsFoundCallback;
			engine.AttachNode (player);

			var fileUrl = NSBundle.MainBundle.GetUrlForResource ("drumLoop", "caf");
			if (fileUrl == null)
				throw new NullReferenceException ("drumploop.caf file not found");

			SetPlayerFile (fileUrl);

			if (componentsFoundCallback != null) {
				UpdateEffectsList ();
				NSNotificationCenter.DefaultCenter.AddObserver (AUAudioUnit.AudioComponentRegistrationsChangedNotification,
					null, null, notification => UpdateEffectsList ());
			}

#if __IOS__
			try {
				AVAudioSession.SharedInstance ().SetCategory (AVAudioSessionCategory.Playback);
			} catch {
				throw new Exception ("Can't set Audio Session category");
			}
#endif

			NSNotificationCenter.DefaultCenter.AddObserver (
				AUAudioUnit.AudioComponentInstanceInvalidationNotification, null, null, notification => {
					var crashedAU = notification.Object as AUAudioUnit;
					if (AudioUnit == crashedAU)
						SelectEffectWithComponentDescription (null, null);
				}
			);
		}

		void UpdateEffectsList ()
		{
			DispatchQueue.DefaultGlobalQueue.DispatchAsync (() => {
				var anyEffectDescription = new AudioComponentDescription {
					ComponentType = AudioComponentType.Effect,
					ComponentSubType = 0,
					ComponentManufacturer = 0,
					ComponentFlags = 0,
					ComponentFlagsMask = 0
				};

				availableEffects = AVAudioUnitComponentManager.SharedInstance.GetComponents (anyEffectDescription).ToList ();
				DispatchQueue.MainQueue.DispatchAsync (ComponentsFoundCallback);
			});
		}

		void SetPlayerFile (NSUrl fileUrl)
		{
			NSError error;
			file = new AVAudioFile (fileUrl, out error);
			engine.Connect (player, engine.MainMixerNode, file.ProcessingFormat);

			if (error != null)
				throw new Exception (string.Format ("Could not create AVAudioFile instance. Error: {0}", error.LocalizedDescription));
		}

		static void SetSessionActive (bool active)
		{
#if __IOS__
			var error = AVAudioSession.SharedInstance ().SetActive (active);
			if (error != null)
				throw new Exception (string.Format ("Could not set Audio Session active {0}.  Error: {1}", active, error.LocalizedDescription));
#endif
		}

		public bool TogglePlay ()
		{
			stateChangeQueue.DispatchSync (() => {
				if (isPlaying) {
					player.Stop ();
					engine.Stop ();
					isPlaying = false;

					SetSessionActive (false);
				} else {
					SetSessionActive (true);

					ScheduleLoop ();
					ScheduleLoop ();

					NSError error;
					try {
						engine.StartAndReturnError (out error);
						if (error != null)
							throw new Exception (string.Format ("Could not start engine.  Error: {0}", error.LocalizedDescription));
					} catch (Exception e) {
						Console.WriteLine (e.Message);
					}
					player.Play ();
					isPlaying = true;
				}
			});

			return isPlaying;
		}

		void ScheduleLoop ()
		{
			if (file == null)
				throw new NullReferenceException ("`file` must not be null in \"scheduleLoop\"");

			player.ScheduleFile (file, null, () =>
				stateChangeQueue.DispatchAsync (() => {
				if (isPlaying)
					ScheduleLoop ();
				})
			);
		}

		public void SelectPresetIndex (int presetIndex)
		{
			if (AudioUnit == null)
				return;

			AudioUnit.CurrentPreset = PresetList [presetIndex];
		}

		public void SelectEffectComponent (AVAudioUnitComponent component, Action completionHandler)
		{
			SelectEffectWithComponentDescription (component != null ?
				component.AudioComponentDescription : (AudioComponentDescription?)null, completionHandler);
		}

		public void SelectEffectWithComponentDescription (AudioComponentDescription? componentDescription, Action completionHandler)
		{
			if (isPlaying)
				player.Pause ();

			if (effect != null) {
				engine.DisconnectNodeInput (effect);
				engine.DisconnectNodeInput (engine.MainMixerNode);

				engine.Connect (player, engine.MainMixerNode, file.ProcessingFormat);

				engine.DetachNode (effect);

				effect = null;
				AudioUnit = null;
				PresetList = new List <AUAudioUnitPreset> ();
			}
				
			if (componentDescription != null) {
				AVAudioUnit.FromComponentDescription (componentDescription.Value, 0, (avAudioUnitEffect, AVError) => {
					if (AVError != null || avAudioUnitEffect == null) {
						Console.WriteLine ("SelectEffectWithComponentDescription error!");
						return;
					}
						
					effect = avAudioUnitEffect;
					engine.AttachNode (avAudioUnitEffect);

					engine.DisconnectNodeInput (engine.MainMixerNode);

					engine.Connect (player, avAudioUnitEffect, file.ProcessingFormat);
					engine.Connect (avAudioUnitEffect, engine.MainMixerNode, file.ProcessingFormat);

					AudioUnit = avAudioUnitEffect.AUAudioUnit;
					PresetList = avAudioUnitEffect.AUAudioUnit.FactoryPresets != null ?
						avAudioUnitEffect.AUAudioUnit.FactoryPresets.ToList () : new List <AUAudioUnitPreset> ();
					Done (completionHandler);
				});
			} else {
				Done (completionHandler);
			}
		}

		void Done (Action completionHandler)
		{
			if (isPlaying)
				player.Play ();

			if (completionHandler != null)
				completionHandler ();
		}
	}
}
