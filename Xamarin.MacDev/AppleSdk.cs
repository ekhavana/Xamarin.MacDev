﻿// 
// AppleSdk.cs
//  
// Authors: Rolf Bjarne Kvinge <rolf@xamarin.com>
//
// Copyright (c) 2015 Xamarin Inc.
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Xamarin.MacDev
{
	public abstract class AppleSdk
	{
		List<IPhoneSdkVersion> knownOSVersions = new List<IPhoneSdkVersion> ();

		public string DeveloperRoot { get; protected set; }
		public string VersionPlist { get; protected set; }

		protected abstract string DevicePlatformName { get; }
		protected abstract string SimulatorPlatformName { get; }
		protected abstract List<IPhoneSdkVersion> InitiallyKnownOSVersions { get; }

		public string DevicePlatform { get { return Path.Combine (DeveloperRoot, "Platforms/" + DevicePlatformName + ".platform"); } }
		public string SimPlatform { get { return Path.Combine (DeveloperRoot, "Platforms/" + SimulatorPlatformName + ".platform"); } }

		public bool IsInstalled { get; private set; }
		public IPhoneSdkVersion[] InstalledSdkVersions { get; private set; }
		public IPhoneSdkVersion[] InstalledSimVersions { get; private set; }

		readonly Dictionary<string, AppleDTSdkSettings> sdkSettingsCache = new Dictionary<string, AppleDTSdkSettings> ();
		readonly Dictionary<string, AppleDTSdkSettings> simSettingsCache = new Dictionary<string, AppleDTSdkSettings> ();
		AppleDTSettings dtSettings;

		const string PLATFORM_VERSION_PLIST = "version.plist";
		const string SYSTEM_VERSION_PLIST = "/System/Library/CoreServices/SystemVersion.plist";

		protected void Init ()
		{
			IsInstalled = File.Exists (Path.Combine (DevicePlatform, "Info.plist"));
			IEnumerable<IPhoneSdkVersion> olderSdkVersions;
			if (IsInstalled) {
				File.GetLastWriteTime (VersionPlist);
				InstalledSdkVersions = EnumerateSdks (Path.Combine (DevicePlatform, "Developer/SDKs"), DevicePlatformName);
				InstalledSimVersions = EnumerateSdks (Path.Combine (SimPlatform, "Developer/SDKs"), SimulatorPlatformName);

				// We don't show known versions (beta) higher than the installed sdk version (current Xcode).
				olderSdkVersions = InitiallyKnownOSVersions.Where(x => x < InstalledSdkVersions[0]);
			} else {
				InstalledSdkVersions = new IPhoneSdkVersion[0];
				InstalledSimVersions = new IPhoneSdkVersion[0];
				olderSdkVersions = Enumerable.Empty<IPhoneSdkVersion> ();
			}

			knownOSVersions = olderSdkVersions
				.Union (InstalledSdkVersions)
				.ToList ();
			knownOSVersions.Sort ();
		}


		public string GetPlatformPath (bool sim)
		{
			return sim ? SimPlatform : DevicePlatform;
		}

		public string GetSdkPath (IPhoneSdkVersion version, bool sim)
		{
			return GetSdkPath (version.ToString (), sim);
		}

		public string GetSdkPath (string version, bool sim)
		{
			if (sim)
				return Path.Combine (SimPlatform, "Developer/SDKs/" + SimulatorPlatformName + version + ".sdk");

			return Path.Combine (DevicePlatform, "Developer/SDKs/" + DevicePlatformName + version + ".sdk");
		}

		string GetSdkPlistFilename (string version, bool sim)
		{
			return Path.Combine (GetSdkPath (version, sim), "SDKSettings.plist");
		}

		public bool SdkIsInstalled (IPhoneSdkVersion version, bool sim)
		{
			foreach (var v in (sim? InstalledSimVersions : InstalledSdkVersions))
				if (v.Equals (version))
					return true;
			return false;
		}

		public AppleDTSdkSettings GetSdkSettings (IPhoneSdkVersion sdk, bool isSim)
		{
			var cache = isSim ? simSettingsCache : sdkSettingsCache;

			AppleDTSdkSettings settings;
			if (cache.TryGetValue (sdk.ToString (), out settings))
				return settings;

			try {
				settings = LoadSdkSettings (sdk, isSim);
			} catch (Exception ex) {
				var sdkName = isSim ? SimulatorPlatformName : DevicePlatformName;
				LoggingService.LogError (string.Format ("Error loading settings for SDK {0} {1}", sdkName, sdk), ex);
			}

			cache[sdk.ToString ()] = settings;
			return settings;
		}

		AppleDTSdkSettings LoadSdkSettings (IPhoneSdkVersion sdk, bool isSim)
		{
			var settings = new AppleDTSdkSettings ();

			var plist = PDictionary.FromFile (GetSdkPlistFilename (sdk.ToString (), isSim));
			if (!isSim)
				settings.AlternateSDK = plist.GetString ("AlternateSDK").Value;

			settings.CanonicalName = plist.GetString ("CanonicalName").Value;

			var props = plist.Get<PDictionary> ("DefaultProperties");

			PString gcc;
			if (!props.TryGetValue<PString> ("GCC_VERSION", out gcc))
				settings.DTCompiler = "com.apple.compilers.llvm.clang.1_0";
			else
				settings.DTCompiler = gcc.Value;

			settings.DeviceFamilies = props.GetUIDeviceFamily ("SUPPORTED_DEVICE_FAMILIES");
			if (!isSim) {
				var plstPlist = Path.Combine (GetPlatformPath (isSim), PLATFORM_VERSION_PLIST);
				settings.DTSDKBuild = GrabRootString (plstPlist, "ProductBuildVersion");
			}

			return settings;
		}

		public AppleDTSettings GetDTSettings ()
		{
			if (dtSettings != null)
				return dtSettings;

			var dict = PDictionary.FromFile (Path.Combine (DevicePlatform, "Info.plist"));
			var infos = dict.Get<PDictionary> ("AdditionalInfo");
			var systemVersionPlist = Path.Combine (DeveloperRoot, SYSTEM_VERSION_PLIST);

			return (dtSettings = new AppleDTSettings {
				DTPlatformVersion = infos.Get<PString> ("DTPlatformVersion").Value,
				DTPlatformBuild = GrabRootString (Path.Combine (DevicePlatform, "version.plist"), "ProductBuildVersion"),
				DTXcodeBuild = GrabRootString (VersionPlist, "ProductBuildVersion"),
				BuildMachineOSBuild = GrabRootString (systemVersionPlist, "ProductBuildVersion"),
			});
		}

		public IPhoneSdkVersion GetClosestInstalledSdk (IPhoneSdkVersion v, bool sim)
		{
			//sorted low to high, so get first that's >= requested version
			foreach (var i in GetInstalledSdkVersions (sim)) {
				if (i.CompareTo (v) >= 0)
					return i;
			}
			return IPhoneSdkVersion.UseDefault;
		}

		public IList<IPhoneSdkVersion> GetInstalledSdkVersions (bool sim)
		{
			return sim ? InstalledSimVersions : InstalledSdkVersions;
		}

		public IList<IPhoneSdkVersion> KnownOSVersions { get { return knownOSVersions; } }

		protected static IPhoneSdkVersion[] EnumerateSdks (string sdkDir, string name)
		{
			if (!Directory.Exists (sdkDir))
				return new IPhoneSdkVersion[0];

			var sdks = new List<string> ();

			foreach (var dir in Directory.GetDirectories (sdkDir)) {
				if (!File.Exists (Path.Combine (dir, "SDKSettings.plist")))
					continue;

				string d = Path.GetFileName (dir);
				if (!d.StartsWith (name, StringComparison.Ordinal))
					continue;

				d = d.Substring (name.Length);
				if (!d.EndsWith (".sdk", StringComparison.Ordinal))
					continue;

				d = d.Substring (0, d.Length - ".sdk".Length);
				if (d.Length > 0)
					sdks.Add (d);
			}

			var vs = new List<IPhoneSdkVersion> ();
			foreach (var s in sdks) {
				try {
					vs.Add (IPhoneSdkVersion.Parse (s));
				} catch (Exception ex) {
					LoggingService.LogError ("Could not parse {0} SDK version '{1}':\n{2}", name, s, ex.ToString ());
				}
			}

			var versions = vs.ToArray ();
			Array.Sort (versions);
			return versions;
		}

		protected static string GrabRootString (string file, string key)
		{
			if (!File.Exists (file))
				return null;

			var dict = PDictionary.FromFile (file);
			PString value;

			if (dict.TryGetValue<PString> (key, out value))
				return value.Value;

			return null;
		}
	}
}
