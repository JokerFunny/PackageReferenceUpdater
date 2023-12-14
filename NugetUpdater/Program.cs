using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Newtonsoft.Json.Linq;

using NuGet.Versioning;

using static System.Net.Mime.MediaTypeNames;

class Program
{
	private static readonly Regex s_rNuGetInstallOutputRegex = new Regex(@"(Retrieving|Found) package '(?<name>[^']+)\s(?<version>[^']+)'", RegexOptions.Compiled);
	private static readonly List<string> s_rPathsToUpdate = new List<string>();

	private static readonly Action<string> s_rErrorDataHandler = Console.WriteLine;

	private const int TF_ARGUMENT_BLOCK_SIZE = 100;

	static void Main(string[] args)
	{
		if (args.Length < 3)
		{
			Console.WriteLine("Usage: UpdatePackagesAndRedirects \"<BasePath>\" \"<PackageNames>\" \"<PackageVersion>\"");
			return;
		}

		string basePath = args[0];
		string[] packageNames = args[1].Split(' ');
		string packageVersion = args[2];
		string tfPath = args[3];

		string[] csprojFiles = Directory.GetFiles(basePath, "*.csproj", SearchOption.AllDirectories);

		// Step 1: Update package versions in csproj files.
		UpdatePackageVersions(csprojFiles, packageNames, packageVersion);

		// Step 2: Get the versions to apply for binding redirects.
		List<NuGetPackage> bindingRedirects = GetBindingRedirectVersions(csprojFiles, packageNames, packageVersion);

		// Step 3: Apply binding redirects in app.config/web.config.
		ApplyBindingRedirects(csprojFiles, bindingRedirects);

		if (!string.IsNullOrEmpty(tfPath))
		{
			CheckOutChangedFIles(tfPath);
		}

		Console.WriteLine("Package versions updated and binding redirects applied successfully.");
	}

	static void UpdatePackageVersions(string[] csprojFiles, string[] packageNames, string packageVersion)
	{
		foreach (var csprojFile in csprojFiles)
		{
			string content = File.ReadAllText(csprojFile);
			string updatedContent = string.Empty;

			foreach (var packageName in packageNames)
			{
				// Updated regular expression to handle both short and long versions.
				string multiLinePattern = $@"<PackageReference Include=""{packageName}""[^>]*>\s*<Version>[^<]*<\/Version>\s*<\/PackageReference>";
				string singleLinePattern = $"<PackageReference Include=\"{packageName}\" Version=\"[^\"]+\".*?>";

				// We'll use a single line format, which is a more modern.
				string replacement = $"<PackageReference Include=\"{packageName}\" Version=\"{packageVersion}\" />";

				updatedContent = Regex.Replace(content, singleLinePattern, replacement);
				updatedContent = Regex.Replace(updatedContent, multiLinePattern, replacement, RegexOptions.Singleline);
			}

			if (content != updatedContent)
			{
				try
				{
					ClearAttributes(csprojFile);
					File.WriteAllText(csprojFile, updatedContent);

					Console.WriteLine($"Updated [{csprojFile}] with project(s): [{string.Join("; ", packageNames)}] with version: [{packageVersion}].");

					s_rPathsToUpdate.Add(csprojFile);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Update of the file [{csprojFile}] failed! Error message: [{ex.Message}].");
				}
			}
			else
			{
				Console.WriteLine($"No changes made to [{csprojFile}].");
			}
		}
	}

	static List<NuGetPackage> GetBindingRedirectVersions(string[] csprojFiles, string[] packageNames, string packageVersion)
	{
		Console.WriteLine("Step 2.1: Get inner dependencies of the specified package version.");
		List<NuGetPackage> dependenciesToUpgrade = [];
		foreach (var packageName in packageNames)
		{
			List<NuGetPackage> innerDependencies = GetInnerDependencies(packageName, packageVersion);
			foreach (NuGetPackage dependency in innerDependencies)
			{
				NuGetPackage existingItem = dependenciesToUpgrade.FirstOrDefault(x => x.Name == dependency.Name);
				if (existingItem != null)
				{
					// Update the existing version if the new version is greater.
					if (CompareVersions(dependency.Version, existingItem.Version) > 0)
					{
						existingItem.Version = dependency.Version;
					}
				}
				else
					dependenciesToUpgrade.Add(dependency);
			}
		}

		Console.WriteLine("Step 2.2: Get existing versions from project.assets.json.");
		List<string> assetsFilePaths = [];
		foreach (var csprojFile in csprojFiles)
		{
			string directoryPath = Path.GetDirectoryName(csprojFile);
			string assetsFilePath = Path.Combine(directoryPath, "obj", "project.assets.json");
			if (File.Exists(assetsFilePath))
				assetsFilePaths.Add(assetsFilePath);
		}

		Dictionary<string, string> existingVersions = [];
		foreach (string assetsFilePath in assetsFilePaths)
			GetExistingVersions(assetsFilePath, existingVersions);

		// TRACING.
		Console.WriteLine("Package (with immer dependencies) versions:");
		foreach (var item in dependenciesToUpgrade)
			Console.WriteLine($"Name: [{item.Name}], version: [{item.Version}].");

		Console.WriteLine($"Found {existingVersions.Count} dependencies from 'project.assets.json' file(s).");

		Console.WriteLine("Step 2.3: Decide on the version to apply for binding redirect.");
		DecideBindingRedirectVersion(dependenciesToUpgrade, existingVersions);

		UpdateFullPackageVersions(dependenciesToUpgrade, packageNames, packageVersion);

		// TRACING.
		Console.WriteLine($"{Environment.NewLine}Versions to apply:");
		foreach (var item in dependenciesToUpgrade)
			Console.WriteLine($"Name: [{item.Name}], version: [{item.Version}], full version: [{item.FullVersion}].");

		return dependenciesToUpgrade;
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
				throw new InvalidOperationException($"The command 'nuget $\"install {{packageName}} -Version {{packageVersion}} -OutputDirectory {{tempDir}} -Verbosity detailed\"' exited with error code {process.ExitCode}");
		}

		return result;
	}

	static List<NuGetPackage> GetInnerDependencies(string packageName, string packageVersion)
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"{packageName}_{Guid.NewGuid()}");
		Directory.CreateDirectory(tempDir);

		try
		{
			string output = GetPackageFromNuget(packageName, packageVersion, tempDir);

			return ParseNuGetOutput(output);
		}
		finally
		{
			ClearAttributes(tempDir);
			Directory.Delete(tempDir, true);
		}
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

	static void GetExistingVersions(string assetsFilePath, Dictionary<string, string> existingVersions)
	{
		void __AddItemToCollection(Dictionary<string, string> targetCollection, string dependencyName, string versionToApply)
		{
			// Add dependency to dictionary
			if (!string.IsNullOrEmpty(versionToApply))
			{
				if (targetCollection.TryGetValue(dependencyName, out string existingVersion))
				{
					// Entry already exists; compare versions and update if the new version is greater
					if (CompareVersions(versionToApply, existingVersion) > 0)
					{
						targetCollection[dependencyName] = versionToApply;
					}
				}
				else
				{
					// Entry doesn't exist; add it to the dictionary
					targetCollection[dependencyName] = versionToApply;
				}
			}
		}

		string __ExtractVersion(JToken versionToken)
		{
			if (versionToken == null)
				return string.Empty;

			string versionString = versionToken.ToString();

			// Check if the version is a range
			if (versionString.StartsWith("[") || versionString.StartsWith("(")
				|| versionString.EndsWith("]") || versionString.EndsWith(")"))
			{
				// Parse version range
				VersionRange versionRange = VersionRange.Parse(versionString);

				// Take the lower bound of the version range
				return versionRange.MaxVersion.Version.ToString();
			}

			return versionString;
		}

		try
		{
			string jsonContent = File.ReadAllText(assetsFilePath);
			JObject jsonObject = JObject.Parse(jsonContent);

			foreach (var target in jsonObject["targets"].Children<JProperty>())
			{
				foreach (var package in target.Value.Children<JProperty>())
				{
					string[] packageNameElementParts = package.Name?.Split('/');
					var packageName = packageNameElementParts[0];
					string packageVersion = packageNameElementParts[1];

					// Add package to dictionary
					__AddItemToCollection(existingVersions, packageName, packageVersion);

					// Extract dependencies for the package
					var packageDependencies = package.Value["dependencies"]?.Children<JProperty>();
					if (packageDependencies != null)
					{
						foreach (var dependency in packageDependencies)
						{
							string dependencyName = dependency.Name;
							string dependencyVersion = __ExtractVersion(dependency.Value);

							__AddItemToCollection(existingVersions, dependencyName, dependencyVersion);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error parsing JSON: [{ex.Message}].");
		}
	}

	static void DecideBindingRedirectVersion(List<NuGetPackage> dependenciesToUpgrade, Dictionary<string, string> existingVersions)
	{
		// Use the existingVersions dictionary to decide the binding redirect version.
		foreach (var targetDependency in dependenciesToUpgrade)
		{
			if (existingVersions.TryGetValue(targetDependency.Name, out string existingVersion))
			{
				// Update the existing version if the new version is greater.
				if (CompareVersions(existingVersion, targetDependency.Version) > 0)
				{
					targetDependency.Version = existingVersion;
				}
			}
		}
	}

	static void UpdateFullPackageVersions(List<NuGetPackage> bindingRedirects, string[] packageNames, string packageVersion)
	{
		foreach (string packageName in packageNames)
			UpdateFullPackageVersionsBasedOnNugetPackage(bindingRedirects, packageName, packageVersion);

		List<NuGetPackage> packagesToProceed = bindingRedirects.Where(p => string.IsNullOrEmpty(p.FullVersion)).ToList();
		foreach (NuGetPackage packageToProceed in packagesToProceed)
		{
			if (string.IsNullOrEmpty(packageToProceed.FullVersion))
				UpdateFullPackageVersionsBasedOnNugetPackage(bindingRedirects, packageToProceed.Name, packageToProceed.Version);

			// That could be that one of the packages updates other(s), so we need to ensure that there is any package for update left.
			if (!bindingRedirects.Where(p => string.IsNullOrEmpty(p.FullVersion)).Any())
				break;
		}
	}

	static void UpdateFullPackageVersionsBasedOnNugetPackage(List<NuGetPackage> bindingRedirects, string packageName, string packageVersion)
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"{packageName}_{Guid.NewGuid()}");
		Directory.CreateDirectory(tempDir);

		try
		{
			string output = GetPackageFromNuget(packageName, packageVersion, tempDir);

			List<NuGetPackage> nugetCommandResults = ParseNuGetOutput(output);

			foreach (NuGetPackage nuGetPackage in nugetCommandResults)
			{
				NuGetPackage existingPackageInfo = bindingRedirects.FirstOrDefault(x => x.Name == nuGetPackage.Name);
				if (!string.IsNullOrEmpty(existingPackageInfo?.FullVersion))
				{
					// Skip the proceeding if the 'FullVersion' already exists.
					continue;
				}

				if (nuGetPackage.Version != existingPackageInfo.Version)
				{
					// It seems like after parsing the 'project.assets.json' we have another version,
					// so we don't need to proceed here, but we will use NuGet to get the proper version.
					continue;
				}

				// Use 'existingPackageInfo.Version' to try to get a proper package.
				string packageFolderPath = $"{tempDir}\\{nuGetPackage.Name}.{nuGetPackage.Version}";
				if (Directory.Exists(packageFolderPath))
				{
					string assemblyFullPath = Directory.GetFiles(packageFolderPath, $"{nuGetPackage.Name}.dll", SearchOption.AllDirectories).FirstOrDefault();
					if (File.Exists(assemblyFullPath))
					{
						// Get the assembly name without loading the assembly
						AssemblyName assemblyName = AssemblyName.GetAssemblyName(assemblyFullPath);

						// Access the version information
						string assemblyVersion = assemblyName.Version.ToString();

						existingPackageInfo.FullVersion = assemblyVersion;
					}
				}
				// Otherwise, all empty lines (NuGetPackage.FullVersion) would be treated as packages for additional checks.
			}
		}
		finally
		{
			ClearAttributes(tempDir);
			Directory.Delete(tempDir, true);
		}
	}

	static void ApplyBindingRedirects(string[] csprojFiles, List<NuGetPackage> bindingRedirectVersions)
	{
		// Get config file paths.
		List<string> configFilePaths = [];
		foreach (var csprojFile in csprojFiles)
		{
			string directoryPath = Path.GetDirectoryName(csprojFile);

			string appConfigFilePath = Path.Combine(directoryPath, "app.config");
			if (File.Exists(appConfigFilePath))
				configFilePaths.Add(appConfigFilePath);

			string webConfigFilePath = Path.Combine(directoryPath, "web.config");
			if (File.Exists(webConfigFilePath))
				configFilePaths.Add(webConfigFilePath);
		}

		XNamespace ns = "urn:schemas-microsoft-com:asm.v1";

		foreach (string configFile in configFilePaths)
		{
			XDocument doc = XDocument.Load(configFile);

			// Find all dependentAssembly nodes with bindingRedirects using the namespace.
			IEnumerable<XElement> dependentAssemblyNodes = doc.Descendants(ns + "dependentAssembly");

			bool fileChanged = false;

			foreach (NuGetPackage bindingRedirect in bindingRedirectVersions)
			{
				// Find the specific dependentAssembly node by assemblyIdentity name.
				XElement bindingRedirectNode = dependentAssemblyNodes
					.FirstOrDefault(dependentAssembly =>
						dependentAssembly.Element(ns + "assemblyIdentity")?.Attribute("name")?.Value == bindingRedirect.Name);

				if (bindingRedirectNode != null)
				{
					// Get the existing version from the newVersion attribute.
					string existingVersion = bindingRedirectNode.Element(ns + "bindingRedirect")?.Attribute("newVersion")?.Value;

					// Check if the new version is greater than the existing one.
					if (CompareVersions(bindingRedirect.FullVersion, existingVersion) > 0)
					{
						// Update oldVersion attribute with a new top version
						string oldVersion = bindingRedirectNode.Element(ns + "bindingRedirect")?.Attribute("oldVersion")?.Value;
						if (!string.IsNullOrEmpty(oldVersion))
						{
							string[] versions = oldVersion.Split('-');
							if (versions.Length > 1)
							{
								versions[1] = bindingRedirect.FullVersion;
								bindingRedirectNode.Element(ns + "bindingRedirect")?.SetAttributeValue("oldVersion", string.Join("-", versions));
							}
						}

						// Update newVersion attribute.
						bindingRedirectNode.Element(ns + "bindingRedirect")?.SetAttributeValue("newVersion", bindingRedirect.FullVersion);

						fileChanged = true;
					}
				}
			}

			// Save the changes only if there are modifications.
			if (fileChanged)
			{
				doc.Save(configFile);
				Console.WriteLine($"Changes saved to [{configFile}].");

				s_rPathsToUpdate.Add(configFile);
			}
			else
			{
				Console.WriteLine($"No changes made to [{configFile}].");
			}
		}
	}

	static void CheckOutChangedFIles(string tfToolPath)
	{
		void __CheckOutFilesWithTF(string tfToolPath, string[] fileFullPaths)
		{
			string processArguments = $"checkout {string.Join(" ", fileFullPaths)}";

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

			//if (process.ExitCode != 0)
			//{
			//	throw new InvalidOperationException($"The command [{tfToolPath}] with arguments [{processArguments}] exited with error code {process.ExitCode}");
			//}
		}

		if (!File.Exists(tfToolPath))
			throw new InvalidOperationException($"Could not find command line tool [{tfToolPath}]!");

		int cycles = (int)Math.Ceiling((double)s_rPathsToUpdate.Count / TF_ARGUMENT_BLOCK_SIZE);

		for (int i = 0; i < cycles; i++)
		{
			int elementsToSkip = i * 100;

			string[] fileFullPathsForCommand = s_rPathsToUpdate.Skip(elementsToSkip)
				.Take(100)
				.Select(x => $"\"{x}\"")
				.ToArray();

			__CheckOutFilesWithTF(tfToolPath, fileFullPathsForCommand);
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
		// Version strings are in the format "x.y.z.a".
		// Convert version strings to Version objects for comparison.
		Version v1 = new Version(version1);
		Version v2 = new Version(version2);

		return v1.CompareTo(v2);
	}

	class NuGetPackage
	{
		public string Name { get; set; }
		public string Version { get; set; }
		public string FullVersion { get; set; }
	}
}
