# PackageReferenceUpdater
 - Tool to update PackageReference + existing BindingRedirects with a proper version.

## The logic is the next:

### Step 1: Update package versions in csproj files:
-- Using passed base path to retrieve all '.csproj' files under it;

-- Update 'PackageReference' element for the target package(s) using target version;

-- Save changed file(s).

### Step 2: Get the versions to apply for binding redirects:
-- Gets target package(s) using the 'NuGet install' command to get all inner dependencies with the version of the target packages;

-- Use .csproj files to get 'project.assets.json' files to parse it for the versions that exist under the target path (it's expected that you don't have a "versions hell" and they are aligned between projects) - get all packages with the max version that could be used;

-- Decide which version should be applied to the target packages (received from the 'NuGet install' command) with the info from project.assets.json file(s);

-- Get the package(s) full version using the 'NuGet install' command to proceed all packages properly.

### Step 3: Apply binding redirects in app.config/web.config:
-- Get the app.config/web.config file paths using .csproj file path(s);

-- Using the built collection, update any existing 'bindingRedirect' with the new version: updates 'oldVersion' + 'newVersion' elements with a proper new max used version;

-- Save changed file(s).

## You need to pass the next params:

### UpdatePackagesAndRedirects.exe "`BasePath`" "`PackageNames`" "`PackageVersion`"
-- For `BasePath` - it should be a full path of the top folder of your project(s);

-- For `PackageNames` - target packages) to be updated, separated by a space (' ');

-- For `PackageVersion` - a version that needs to be applied.
