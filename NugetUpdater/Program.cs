using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using CommandLine;

using Newtonsoft.Json.Linq;

using NuGet.Versioning;

class Program
{
	private static readonly Regex s_rNuGetInstallOutputRegex = new Regex(@"(Retrieving|Found) package '(?<name>[^']+)\s(?<version>[^']+)'", RegexOptions.Compiled);
	private static readonly Action<string> s_rErrorDataHandler = Console.WriteLine;

	private static readonly List<string> s_rPathsToUpdate = [];
	private static readonly List<string> s_rPathsToAdd = [];

	private const int TF_ARGUMENT_BLOCK_SIZE = 100;

	private const string DEFAULT_APP_CONFIG_CONTENT = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <runtime>
    <assemblyBinding xmlns=""urn:schemas-microsoft-com:asm.v1"">
    </assemblyBinding>
  </runtime>
</configuration>";

	static void Main(string[] args)
	{
		Options passedParams = Parser.Default.ParseArguments<Options>(args)
			.WithNotParsed(HandleParseError)
			.Value;

		List<ProjectInfo> projectInfos = CollectProjectInfos(passedParams.BasePath, passedParams.Detailed);

		Dictionary<string, NuGetPackage> existingVersions = GetExistingPackagesWithVersions(projectInfos, passedParams.Detailed);

		if (!passedParams.ExplicitVersion)
		{
			// If 'ExplicitVersion' is not set, align package versions to the latest existing.
			DecideBindingRedirectVersions(projectInfos, existingVersions, passedParams.Detailed);
		}

		GetFullPackagesInfo(projectInfos, existingVersions, passedParams.Detailed);

		ApplyBindingRedirects(projectInfos);

		if (!string.IsNullOrEmpty(passedParams.TfPath))
			CheckOutChangedFIles(passedParams.TfPath, passedParams.Detailed);

		Console.WriteLine("Package versions updated and binding redirects applied successfully.");
	}

	static List<ProjectInfo> CollectProjectInfos(string basePath, bool detailed)
	{
		Console.WriteLine($"Step 1: Collect project files info.");
		List<ProjectInfo> result = [];

		string[] csprojFiles = Directory.GetFiles(basePath, "*.csproj", SearchOption.AllDirectories);
		foreach (var csprojFile in csprojFiles)
		{
			string directoryPath = Path.GetDirectoryName(csprojFile);

			var projectToProcess = new ProjectInfo()
			{
				Name = Path.GetFileNameWithoutExtension(csprojFile),
				ProjectFilePath = csprojFile,
				AssetsFilePath = Path.Combine(directoryPath, "obj", "project.assets.json")
			};

			List<string> configFilePaths = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
				.Where(file => !file.Contains("\\bin\\"))
				.Where(file => file.EndsWith("app.config", StringComparison.OrdinalIgnoreCase) || file.EndsWith("web.config", StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (configFilePaths.Count == 0)
			{
				projectToProcess.AddNewAppConfigFile = true;
				projectToProcess.ConfigFilePaths = [Path.Combine(directoryPath, "app.config")];
			}
			else
			{
				// Add additional config file paths if any.
				projectToProcess.ConfigFilePaths = new List<string>(configFilePaths);
			}

			result.Add(projectToProcess);
		}

		Console.WriteLine($"Found [{result.Count}] projects to process");

		if (detailed)
		{
			foreach (var projectInfo in result)
			{
				Console.WriteLine($"{Environment.NewLine}Project name: [{projectInfo.Name}];" +
					$"{Environment.NewLine}project file path: [{projectInfo.ProjectFilePath}];" +
					$"{Environment.NewLine}project.assets.json file path: [{projectInfo.AssetsFilePath}];" +
					$"{Environment.NewLine}config file path(s) to be {(projectInfo.AddNewAppConfigFile ? "created" : "updated")}: " +
					$"[{Environment.NewLine}    {string.Join($";{Environment.NewLine}    ", projectInfo.ConfigFilePaths)}{Environment.NewLine}].");
			}
		}
		else
			Console.WriteLine($"Project names: [{string.Join("; ", result.Select(x => x.Name))}]");

		return result;
	}

	static Dictionary<string, NuGetPackage> GetExistingPackagesWithVersions(List<ProjectInfo> projectInfos, bool detailed)
	{
		Console.WriteLine($"{Environment.NewLine}Step 2: Get existing versions from project.assets.json.");
		Dictionary<string, NuGetPackage> existingVersions = [];

		foreach (ProjectInfo projectInfo in projectInfos)
		{
			if (File.Exists(projectInfo.AssetsFilePath))
			{
				Dictionary<string, NuGetPackage> dependentPackages = GetExistingPackageVersions(projectInfo.AssetsFilePath, existingVersions, detailed);
				projectInfo.UsedPackages = dependentPackages.Select(x => x.Value).ToList();

				if (detailed)
				{
					Console.WriteLine($"{Environment.NewLine}Total packages count: {existingVersions.Count};" +
						$"{Environment.NewLine}Project [{projectInfo.Name}] dependencies:" +
						$"{Environment.NewLine}[");

					foreach (NuGetPackage dependentPackage in projectInfo.UsedPackages)
						Console.WriteLine($"    Name: [{dependentPackage.Name}], version: [{dependentPackage.Version}];");

					Console.WriteLine("]");
				}
				else
					Console.WriteLine($"Project [{projectInfo.Name}] was processed: found [{projectInfo.UsedPackages.Count}] dependant packages.");
			}
			else
				Console.WriteLine($"project.assets.json file by path [{projectInfo.AssetsFilePath}] doesn't exist. Seems like the project [{projectInfo.Name}] haven't built yet!");
		}

		return existingVersions;
	}

	static string GetPackageFromNuget(string packageName, string packageVersion, string outputDirectory)
	{
		string result = string.Empty;

		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardInput = true,
			RedirectStandardError = true,
			FileName = "nuget",
			Arguments = $"install {packageName} -Version {packageVersion} -OutputDirectory {outputDirectory} -Verbosity detailed"
		};

		using (var process = new Process())
		{
			process.StartInfo = processStartInfo;

			process.Start();

			process.ErrorDataReceived += (sender, dataReceivedEventArgs) =>
			{
				if (!string.IsNullOrEmpty(dataReceivedEventArgs.Data) && s_rErrorDataHandler != null)
				{
					s_rErrorDataHandler(dataReceivedEventArgs.Data);
				}
			};

			process.Start();

			result = process.StandardOutput.ReadToEnd();
			process.BeginErrorReadLine();

			process.WaitForExit();

			if (process.ExitCode != 0)
				throw new InvalidOperationException($"The command 'nuget install {{packageName}} -Version {{packageVersion}} -OutputDirectory {{tempDir}} -Verbosity detailed' exited with error code {process.ExitCode}");
		}

		return result;
	}

	static List<NuGetPackage> ParseNuGetOutput(string output)
	{
		List<NuGetPackage> dependencies = [];

		// Use Regex.Matches to find all matches in the output.
		MatchCollection matches = s_rNuGetInstallOutputRegex.Matches(output);

		// Iterate over matches and extract package name and version.
		foreach (Match match in matches)
		{
			string packageName = match.Groups["name"].Value;
			string packageVersion = match.Groups["version"].Value;

			dependencies.Add(new NuGetPackage
			{
				Name = packageName,
				Version = packageVersion
			});
		}

		return dependencies;
	}

	static Dictionary<string, NuGetPackage> GetExistingPackageVersions(string assetsFilePath, Dictionary<string, NuGetPackage> existingVersions, bool detailed)
	{
		void __AddItemToCollection(Dictionary<string, NuGetPackage> targetCollection, string dependencyName, string versionToApply, VersionRange versionRangeToApply)
		{
			if (!string.IsNullOrEmpty(versionToApply))
			{
				if (targetCollection.TryGetValue(dependencyName, out NuGetPackage existingInfo))
				{
					if (!string.IsNullOrEmpty(existingInfo.Version))
					{
						// Entry already exists; compare versions and update if the new version is greater
						if (CompareVersions(versionToApply, existingInfo.Version) > 0)
						{
							existingInfo.Version = versionToApply;
							targetCollection[dependencyName] = existingInfo;
						}
					}
					else
					{
						existingInfo.Version = versionToApply;
					}
				}
				else
				{
					// Entry doesn't exist; add it to the dictionary
					targetCollection[dependencyName] = new NuGetPackage()
					{
						Name = dependencyName,
						Version = versionToApply
					};
				}
			}

			if (versionRangeToApply != null)
			{
				if (targetCollection.TryGetValue(dependencyName, out NuGetPackage existingInfo))
				{
					if (existingInfo.SupportedVersions != null)
					{
						bool needToChange = false;

						// Build new object with max MinVersion and min MaxVersion.
						NuGetVersion minVersion = existingInfo.SupportedVersions?.MinVersion;
						NuGetVersion maxVersion = existingInfo.SupportedVersions?.MaxVersion;

						if (versionRangeToApply.MinVersion > minVersion)
						{
							minVersion = versionRangeToApply.MinVersion;
							needToChange = true;
						}

						if (versionRangeToApply.MaxVersion < maxVersion)
						{
							maxVersion = versionRangeToApply.MaxVersion;
							needToChange = true;
						}

						if (needToChange)
						{
							VersionRange versionRange = new VersionRange(minVersion: minVersion, maxVersion: maxVersion);

							existingInfo.SupportedVersions = versionRange;
						}
					}
					else
					{
						existingInfo.SupportedVersions = versionRangeToApply;
					}
				}
				else
				{
					// Entry doesn't exist; add it to the dictionary
					targetCollection[dependencyName] = new NuGetPackage()
					{
						Name = dependencyName,
						SupportedVersions = versionRangeToApply
					};
				}
			}
		}

		(string, VersionRange) __ExtractVersion(JToken versionToken)
		{
			if (versionToken == null)
				return (string.Empty, null);

			string versionString = versionToken.ToString();

			// Check if the version is a range
			if (versionString.StartsWith("[") || versionString.StartsWith("(")
				|| versionString.EndsWith("]") || versionString.EndsWith(")"))
			{
				// Parse version range
				VersionRange versionRange = VersionRange.Parse(versionString);

				// Take the lower bound of the version range
				return (string.Empty, versionRange);
			}

			return (versionString, null);
		}

		Dictionary<string, NuGetPackage> result = [];
		Dictionary<string, List<NuGetPackage>> notPackageDependencies = [];

		try
		{
			string jsonContent = File.ReadAllText(assetsFilePath);
			JObject jsonObject = JObject.Parse(jsonContent);

			foreach (var target in jsonObject["targets"].Children<JProperty>())
			{
				foreach (var reference in target.Value.Children<JProperty>())
				{
					string[] referenceNameElementParts = reference.Name?.Split('/');
					var referenceName = referenceNameElementParts[0];
					string referenceVersion = referenceNameElementParts[1];
					string referenceType = reference.Value["type"].ToString();

					// Check that this is a NuGet package.
					if (referenceType == "package")
					{
						// Add package to dictionary
						__AddItemToCollection(result, referenceName, referenceVersion, null);
						__AddItemToCollection(existingVersions, referenceName, referenceVersion, null);

						// Extract dependencies for the package
						var packageDependencies = reference.Value["dependencies"]?.Children<JProperty>();
						if (packageDependencies != null)
						{
							foreach (var dependency in packageDependencies)
							{
								string dependencyName = dependency.Name;
								var dependencyVersions = __ExtractVersion(dependency.Value);

								__AddItemToCollection(result, dependencyName, dependencyVersions.Item1, dependencyVersions.Item2);
								__AddItemToCollection(existingVersions, dependencyName, dependencyVersions.Item1, dependencyVersions.Item2);
							}
						}
					}
					else
					{
						if (detailed)
						{
							if (notPackageDependencies.TryGetValue(referenceType, out List<NuGetPackage> infoToUpdate))
							{
								infoToUpdate.Add(new NuGetPackage()
								{
									Name = referenceName,
									Version = referenceVersion
								});
							}
							else
							{
								notPackageDependencies.Add(referenceType, [
									new NuGetPackage()
									{
										Name = referenceName,
										Version = referenceVersion
									}]);
							}
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogError($"Error parsing JSON [{assetsFilePath}]: [{ex.Message}].");
		}

		if (detailed)
		{
			foreach (KeyValuePair<string, List<NuGetPackage>> referenceType in notPackageDependencies)
			{
				Console.WriteLine($"In the file [{assetsFilePath}] found ['{referenceType.Key}'] which is skipped as we have an aim on 'package' type only. Found packages: [");

				foreach (NuGetPackage dependency in referenceType.Value)
					Console.WriteLine($"    Reference name: [{dependency.Name}], version: [{dependency.Version}];");

				Console.WriteLine("].");
			}
		}

		return result;
	}

	static void DecideBindingRedirectVersions(List<ProjectInfo> projectInfos, Dictionary<string, NuGetPackage> existingPackages, bool detailed)
	{
		Console.WriteLine($"{Environment.NewLine}Step 2.1: Decide on the version to apply for binding redirects.");

		// Use the existingVersions dictionary to decide the binding redirect version.
		foreach (ProjectInfo projectInfo in projectInfos)
		{
			if (detailed)
				Console.WriteLine($"{Environment.NewLine}Updating package versions of the [{projectInfo.Name}] project...");

			foreach (NuGetPackage dependantPackage in projectInfo.UsedPackages)
			{
				NuGetPackage existingInfo = existingPackages[dependantPackage.Name];
				
				// Update the existing version if the new version is greater.
				if (CompareVersions(existingInfo.Version, dependantPackage.Version) > 0)
				{
					if (detailed)
						Console.WriteLine($"Version for the package [{dependantPackage.Name}] updated from [{dependantPackage.Version}] to [{existingInfo.Version}].");

					dependantPackage.Version = existingInfo.Version;
				}

				if (existingInfo.SupportedVersions != null)
					Console.WriteLine($"For the package [{dependantPackage.Name}] there was an supported versions range from 'project.assets.json' file(s): [{existingInfo.SupportedVersions}]; applied version: [{existingInfo.Version}].");
			}
		}
	}

	static void GetFullPackagesInfo(List<ProjectInfo> projectInfos, Dictionary<string, NuGetPackage> existingPackages, bool detailed)
	{
		Stopwatch stopwatch = new();
		stopwatch.Start();
		Console.WriteLine($"{Environment.NewLine}Step 3: Get full packages info.");
		Dictionary<string, List<PackageVersionInfo>> knownPackageVersions = [];
		Dictionary<string, List<string>> knownPackagesToSkip = [];

		// TODO: parallelize.
		// Get full package versions for the all existing packages (if ExplicitVersion is not set, this cycle would be enought).
		foreach (var existingPackage in existingPackages)
		{
			if (knownPackageVersions.TryGetValue(existingPackage.Key, out List<PackageVersionInfo> existingVersionInfos) 
				&& existingVersionInfos.FirstOrDefault(x => x.Version == existingPackage.Value.Version) != null)
			{
				Console.WriteLine($"Full info for [{existingPackage.Key}] package of version [{existingPackage.Value.Version}] already obtained.");
			}
			else if (knownPackagesToSkip.TryGetValue(existingPackage.Key, out List<string> knownVersions) && knownVersions.Contains(existingPackage.Value.Version))
			{
				Console.WriteLine($"[SKIP]: Full info for [{existingPackage.Key}] package of version [{existingPackage.Value.Version}] can't be obtained.");
			}
			else
			{
				Console.WriteLine($"Getting full package info for [{existingPackage.Key}] package of version [{existingPackage.Value.Version}]...");
				UpdatePackageInfosBasedOnNugetPackage(existingPackage.Key, existingPackage.Value.Version, knownPackageVersions, knownPackagesToSkip, detailed);
			}
		}

		foreach (ProjectInfo projectInfo in projectInfos)
		{
			foreach (NuGetPackage packageToProcess in projectInfo.UsedPackages)
			{
				PackageVersionInfo existingVersion = knownPackageVersions[packageToProcess.Name].FirstOrDefault(x => x.Version == packageToProcess.Version);
				if (existingVersion == null)
				{
					if (knownPackagesToSkip.TryGetValue(packageToProcess.Name, out List<string> value) && value.First(x => x == packageToProcess.Version) != null)
					{
						// do nothing...
						// log all skipped packages at the end.
					}
					else
					{
						Console.WriteLine($"[Additional try]: Getting full package info for [{packageToProcess.Name}] package of version [{packageToProcess.Version}]...");
						UpdatePackageInfosBasedOnNugetPackage(packageToProcess.Name, packageToProcess.Version, knownPackageVersions, knownPackagesToSkip, detailed);

						existingVersion = knownPackageVersions[packageToProcess.Name].FirstOrDefault(x => x.Version == packageToProcess.Version);
						if (existingVersion != null)
						{
							packageToProcess.FullVersion = existingVersion.FullVersion;
							packageToProcess.PublicKeyToken = existingVersion.PublicKeyToken;
							packageToProcess.Culture = existingVersion.Culture;
						}
					}
				}
				else
				{
					packageToProcess.FullVersion = existingVersion.FullVersion;
					packageToProcess.PublicKeyToken = existingVersion.PublicKeyToken;
					packageToProcess.Culture = existingVersion.Culture;
				}
			}

			if (detailed)
			{
				Console.WriteLine($"{Environment.NewLine}Project [{projectInfo.Name}] dependencies info:" +
					$"{Environment.NewLine}[");

				foreach (NuGetPackage dependentPackage in projectInfo.UsedPackages)
					Console.WriteLine($"    Name: [{dependentPackage.Name}], version: [{dependentPackage.Version}], full version: [{dependentPackage.FullVersion}], " +
						$"publick key token: [{dependentPackage.PublicKeyToken}], culture: [{dependentPackage.Culture}];");

				Console.WriteLine("]");
			}
			else
				Console.WriteLine($"Project [{projectInfo.Name}] was processed.");
		}

		if (detailed)
		{
			Console.WriteLine($"{Environment.NewLine}Skipped [{knownPackagesToSkip.Count}] dependencies:");

			foreach (string packageName in knownPackagesToSkip.Keys)
				Console.WriteLine($"    Package [{packageName}] skipped with version(s): {string.Join($"; ", knownPackagesToSkip[packageName])}];");
		}

		stopwatch.Stop();
		Console.WriteLine($"Elapsed time: [{stopwatch.Elapsed}]");
	}

	static void UpdatePackageInfosBasedOnNugetPackage(string packageName, string packageVersion, Dictionary<string, List<PackageVersionInfo>> packagesToUpdate, Dictionary<string, List<string>> packagesToSkip, bool detailed)
	{
		static string __GetPublicKeyToken(AssemblyName assemblyName)
		{
			string result = string.Empty;

			var publickKeyTokenBytes = assemblyName.GetPublicKeyToken();

			if (publickKeyTokenBytes?.Length > 0)
			{
				for (int i = 0; i < publickKeyTokenBytes.Length; i++)
					result += string.Format("{0:x2}", publickKeyTokenBytes[i]);
			}

			return result;
		}

		string tempDir = Path.Combine(Path.GetTempPath(), $"{packageName}_{Guid.NewGuid()}");
		Directory.CreateDirectory(tempDir);

		try
		{
			string output = GetPackageFromNuget(packageName, packageVersion, tempDir);

			List<NuGetPackage> nugetCommandResults = ParseNuGetOutput(output);

			foreach (NuGetPackage nuGetPackage in nugetCommandResults)
			{
				if (!packagesToUpdate.ContainsKey(nuGetPackage.Name))
					packagesToUpdate.Add(nuGetPackage.Name, []);

				PackageVersionInfo existingPackageVersionInfo = packagesToUpdate[nuGetPackage.Name].FirstOrDefault(x => x.Version == nuGetPackage.Version);
				if (existingPackageVersionInfo == null)
				{
					// Use 'nuGetPackage.Version' to try to get a proper package version.
					string packageFolderPath = $"{tempDir}\\{nuGetPackage.Name}.{nuGetPackage.Version}";
					if (Directory.Exists(packageFolderPath))
					{
						// TODO: add platform-specific check. Do this after changing the logic of project.assets.json file parsing to contain platform-specific info.
						string assemblyFullPath = Directory.GetFiles(packageFolderPath, $"{nuGetPackage.Name}.dll", SearchOption.AllDirectories).FirstOrDefault();
						if (File.Exists(assemblyFullPath))
						{
							// Get the assembly name without loading the assembly.
							AssemblyName assemblyName = AssemblyName.GetAssemblyName(assemblyFullPath);

							// Access the version information.
							string assemblyVersion = assemblyName.Version.ToString();
							string publicKeyToken = __GetPublicKeyToken(assemblyName);
							string culture = string.IsNullOrEmpty(assemblyName.CultureName) ? "neutral" : assemblyName.CultureName;

							packagesToUpdate[nuGetPackage.Name].Add(new PackageVersionInfo()
							{
								Version = nuGetPackage.Version,
								FullVersion = assemblyVersion,
								PublicKeyToken = publicKeyToken,
								Culture = culture
							});

							if (detailed)
								Console.WriteLine($"    Obtained full info for the package [{nuGetPackage.Name}] with version [{nuGetPackage.Version}]: full package version - [{assemblyVersion}], publick key token - [{publicKeyToken}], culture - [{culture}].");
						}
						else
						{
							if (packagesToSkip.TryGetValue(nuGetPackage.Name, out List<string> versionsToUpdate))
							{
								if (!versionsToUpdate.Contains(nuGetPackage.Version))
									versionsToUpdate.Add(nuGetPackage.Version);
							}
							else
								packagesToSkip.Add(nuGetPackage.Name, [nuGetPackage.Version]);

							if (detailed)
								Console.WriteLine($"    SKIPPED: Full info for the package [{nuGetPackage.Name}] with version [{nuGetPackage.Version}] can't be received, packaged added to exclude list.");
						}
					}
					else
					{
						LogWarning($"Can't find directory by path [{packageFolderPath}]." +
							$"Directory content: [{Environment.NewLine}    {string.Join($";{Environment.NewLine}    ", new DirectoryInfo(tempDir).GetDirectories().Select(x => x.Name))}{Environment.NewLine}]");

						if (packagesToSkip.TryGetValue(nuGetPackage.Name, out List<string> versionsToUpdate))
						{
							if (!versionsToUpdate.Contains(nuGetPackage.Version))
								versionsToUpdate.Add(nuGetPackage.Version);
						}
						else
							packagesToSkip.Add(nuGetPackage.Name, [nuGetPackage.Version]);

						if (detailed)
							Console.WriteLine($"    SKIPPED: Full info for the package [{nuGetPackage.Name}] with version [{nuGetPackage.Version}] can't be received (target folder can't be located), packaged added to exclude list.");
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogError($"Exception during UpdatePackageInfoBasedOnNugetPackage method! Exception message: [{ex.Message}].");
		}
		finally
		{
			try
			{
				ClearAttributes(tempDir);
				Directory.Delete(tempDir, true);
			}
			catch (Exception ex)
			{
				LogError($"Exception during removing the temp folder for the downloaded package by path [{tempDir}]! Exception message: [{ex.Message}].");
			}
		}
	}

	static void ApplyBindingRedirects(List<ProjectInfo> projectInfos)
	{
		Console.WriteLine($"{Environment.NewLine}Step 4: Apply binding redirects in app.config/web.config.");

		foreach (ProjectInfo projectInfo in projectInfos)
		{
			// If the FullVersion is empty - the binding redirect for current package is not applyable -> skip it.
			List<NuGetPackage> bindingRedirectVersionsToApply = projectInfo.UsedPackages
				.Where(x => !string.IsNullOrEmpty(x.FullVersion))
				.ToList();

			XNamespace ns = "urn:schemas-microsoft-com:asm.v1";

			foreach (string configFile in projectInfo.ConfigFilePaths)
			{
				try
				{
					// we consider that this should be with projectInfo.ConfigFilePaths.Count == 0.
					if (projectInfo.AddNewAppConfigFile)
					{
						// we consider this case as a situation that we need to add an app.config file since the web.config file must always exist.
						File.WriteAllText(configFile, DEFAULT_APP_CONFIG_CONTENT);
						Console.WriteLine($"New file [{configFile}] created.");

						s_rPathsToAdd.Add(configFile);

						// [TODO]: add a logic tpo check if app.config reference should be added to the csproj file.
					}

					XDocument doc = XDocument.Load(configFile);

					// Find or create the assemblyBinding element.
					XElement assemblyBindingElement = doc.Descendants(ns + "assemblyBinding").FirstOrDefault();

					if (assemblyBindingElement == null)
					{
						// If assemblyBinding doesn't exist, create it.
						assemblyBindingElement = new XElement(ns + "assemblyBinding");
						doc.Root?.Add(assemblyBindingElement);
					}

					// Find all dependentAssembly nodes with bindingRedirects using the namespace.
					IEnumerable<XElement> dependentAssemblyNodes = doc.Descendants(ns + "dependentAssembly");

					bool fileChanged = bindingRedirectVersionsToApply.Count != 0;

					// Remove existing dependentAssembly elements.
					dependentAssemblyNodes?.Remove();

					foreach (NuGetPackage bindingRedirect in bindingRedirectVersionsToApply)
					{
						XElement newDependentAssembly = new(ns + "dependentAssembly",
						new XElement("assemblyIdentity",
						new XAttribute("name", bindingRedirect.Name),
								new XAttribute("publicKeyToken", bindingRedirect.PublicKeyToken),
								new XAttribute("culture", bindingRedirect.Culture)
							),
							new XElement("bindingRedirect",
							new XAttribute("oldVersion", $"0.0.0.0-{bindingRedirect.FullVersion}"),
									new XAttribute("newVersion", bindingRedirect.FullVersion)
							)
						);

						// Add the new dependentAssembly element to the assemblyBinding element.
						assemblyBindingElement?.Add(newDependentAssembly);
					}

					// Save the changes only if there are modifications.
					if (fileChanged)
					{
						File.SetAttributes(configFile, FileAttributes.Normal);
						using (StreamWriter streamWriter = new(configFile))
						{
							doc.Save(streamWriter);
							streamWriter.Close();
						}
						//doc.Save(configFile);

						Console.WriteLine($"Changes saved to [{configFile}].");

						if (!projectInfo.AddNewAppConfigFile)
							s_rPathsToUpdate.Add(configFile);
					}
					else
					{
						Console.WriteLine($"No changes made to [{configFile}].");
					}
				}
				catch (Exception ex)
				{
					LogError($"Exception during processing with [{configFile}] config! Exception message: [{ex.Message}].");
				}
			}
		}
	}

	static void CheckOutChangedFIles(string tfToolPath, bool detailed)
	{
		void __ExecuteTFCommand(string tfToolPath, string command, string[] fileFullPaths, bool detailed)
		{
			string processArguments = $"{command} {string.Join(" ", fileFullPaths)}";

			if (detailed)
				Console.WriteLine($"Executing the TF command: [{processArguments}].");

			Process process = new();

			ProcessStartInfo processStartInfo = new()
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardInput = true,
				RedirectStandardError = true,
				FileName = tfToolPath,
				WorkingDirectory = Environment.CurrentDirectory,
				Arguments = processArguments
			};

			process.StartInfo = processStartInfo;


			process.ErrorDataReceived += (sender, dataReceivedEventArgs) =>
			{
				if (!string.IsNullOrEmpty(dataReceivedEventArgs.Data) && s_rErrorDataHandler != null)
				{
					s_rErrorDataHandler(dataReceivedEventArgs.Data);
				}
			};

			process.Start();

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			process.WaitForExit();

			if (process.ExitCode != 0)
			{
				LogError($"The command [{tfToolPath}] with arguments [{processArguments}] exited with error code {process.ExitCode}");
				//throw new InvalidOperationException($"The command [{tfToolPath}] with arguments [{processArguments}] exited with error code {process.ExitCode}");
			}
		}

		Console.WriteLine($"{Environment.NewLine}Step 5: Checkout/add files using TF.exe.");

		if (!File.Exists(tfToolPath))
			throw new InvalidOperationException($"Could not find command line tool [{tfToolPath}]!");

		// checkout all changed files.
		int cycles = (int)Math.Ceiling((double)s_rPathsToUpdate.Count / TF_ARGUMENT_BLOCK_SIZE);
		for (int i = 0; i < cycles; i++)
		{
			int elementsToSkip = i * TF_ARGUMENT_BLOCK_SIZE;

			string[] fileFullPathsForCommand = s_rPathsToUpdate.Skip(elementsToSkip)
				.Take(TF_ARGUMENT_BLOCK_SIZE)
				.Select(x => $"\"{x}\"")
				.ToArray();

			__ExecuteTFCommand(tfToolPath, "checkout", fileFullPathsForCommand, detailed);
		}

		// add all new files if any.
		cycles = (int)Math.Ceiling((double)s_rPathsToAdd.Count / TF_ARGUMENT_BLOCK_SIZE);
		for (int i = 0; i < cycles; i++)
		{
			int elementsToSkip = i * TF_ARGUMENT_BLOCK_SIZE;

			string[] fileFullPathsForCommand = s_rPathsToAdd.Skip(elementsToSkip)
				.Take(TF_ARGUMENT_BLOCK_SIZE)
				.Select(x => $"\"{x}\"")
				.ToArray();

			__ExecuteTFCommand(tfToolPath, "add", fileFullPathsForCommand, detailed);
		}
	}

	public static void ClearAttributes(string currentDir)
	{
		if (Directory.Exists(currentDir))
		{
			File.SetAttributes(currentDir, FileAttributes.Normal);

			string[] subDirs = Directory.GetDirectories(currentDir);
			foreach (string dir in subDirs)
			{
				ClearAttributes(dir);
			}

			string[] files = files = Directory.GetFiles(currentDir);
			foreach (string file in files)
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}
		}
	}

	public static int CompareVersions(string version1, string version2)
	{
		bool __IsNumeric(string input)
			=> int.TryParse(input, out _);

		string[] parts1 = version1.Split('.');
		string[] parts2 = version2.Split('.');

		int minLength = Math.Min(parts1.Length, parts2.Length);

		for (int i = 0; i < minLength; i++)
		{
			if (__IsNumeric(parts1[i]) && __IsNumeric(parts2[i]))
			{
				int num1 = int.Parse(parts1[i]);
				int num2 = int.Parse(parts2[i]);

				if (num1 < num2)
				{
					return -1;
				}
				else if (num1 > num2)
				{
					return 1;
				}
			}
			else
			{
				int comparison = string.Compare(parts1[i], parts2[i], StringComparison.OrdinalIgnoreCase);
				if (comparison != 0)
				{
					return comparison;
				}
			}
		}

		// If we haven't returned by now, the versions are equal up to the minimum length
		return parts1.Length.CompareTo(parts2.Length);
	}

	static void LogError(string message)
	{
		Console.BackgroundColor = ConsoleColor.Red;
		Console.WriteLine($"[ERROR]: {message}");
		Console.ResetColor();
	}

	static void LogWarning(string message)
	{
		Console.BackgroundColor = ConsoleColor.DarkYellow;
		Console.WriteLine($"[WARNING]: {message}");
		Console.ResetColor();
	}

	public class NuGetPackage
	{
		public string Name { get; set; }
		public string Version { get; set; }
		public string FullVersion { get; set; }
		public VersionRange SupportedVersions { get; set; }
		public string PublicKeyToken { get; set; }
		public string Culture { get; set; }
	}

	public class PackageVersionInfo
	{
		public string Version { get; set; }
		public string FullVersion { get; set; }
		public string PublicKeyToken { get; set; }
		public string Culture { get; set; }
	}

	public class ProjectInfo
	{
		public string Name { get; set; }
		public string ProjectFilePath { get; set; }
		public string AssetsFilePath { get; set; }
		public List<string> ConfigFilePaths { get; set; } = [];
		public bool AddNewAppConfigFile { get; set; }
		public List<NuGetPackage> UsedPackages { get; set; }
	}

	public class Options
	{
		[Option('b', "basePath", Required = true, HelpText = "Base path")]
		public string BasePath { get; set; }

		[Option('t', "tfPath", Required = false, HelpText = "TF path")]
		public string TfPath { get; set; }

		[Option('d', "detailed", Required = false, Default = false, HelpText = "Enable detailed mode")]
		public bool Detailed { get; set; }

		[Option('d', "explicitVersion", Required = false, Default = false, HelpText = "If the version from the project.assets.json file should be " +
			"explicitly assigned OR should package(s) version should be aligned for all projects from basePath")]
		public bool ExplicitVersion { get; set; }
	}

	static void HandleParseError(IEnumerable<Error> errors)
	{
		Console.WriteLine("Failed to parse command line arguments.");

		foreach (var error in errors)
		{
			switch (error.Tag)
			{
				case ErrorType.BadFormatTokenError:
					Console.WriteLine($"Bad format token error: {error}");
					break;
				case ErrorType.HelpRequestedError:
					Console.WriteLine("Help requested error.");
					break;
				case ErrorType.MissingValueOptionError:
					Console.WriteLine($"Missing value for option: {error}");
					break;
				case ErrorType.NoVerbSelectedError:
					Console.WriteLine("No verb selected error.");
					break;
				case ErrorType.RepeatedOptionError:
					Console.WriteLine($"Repeated option error: {error}");
					break;
				case ErrorType.UnknownOptionError:
					Console.WriteLine($"Unknown option error: {error}");
					break;
				case ErrorType.MissingRequiredOptionError:
					Console.WriteLine($"Missing required option error: {error}");
					break;
				case ErrorType.BadVerbSelectedError:
					Console.WriteLine($"Bad verb selected error: {error}");
					break;
				default:
					Console.WriteLine($"Unknown error type: {error}");
					break;
			}
		}
	}
}
