set windows-shell := ["pwsh.exe", "-NoLogo", "-Command"]

solution := "TemporalAgents.slnx"
configuration := "Release"
artifacts_dir := "artifacts/packages"
coverage_dir := "artifacts/coverage"
unit_tests_dir := "tests/Temporalio.Extensions.Agents.Tests"
integration_tests_dir := "tests/Temporalio.Extensions.Agents.IntegrationTests"
unit_tests_ai_dir := "tests/Temporalio.Extensions.AI.Tests"
integration_tests_ai_dir := "tests/Temporalio.Extensions.AI.IntegrationTests"

version := `dotnet tool run minver --default-pre-release-identifiers preview`

# List available recipes
default:
    @just --list

# Show project info
info:
    @echo "Solution  : {{solution}}"
    @echo "Version   : {{version}}"
    @echo "Config    : {{configuration}}"
    @echo "Artifacts : {{artifacts_dir}}"
    @echo "Coverage  : {{coverage_dir}}"
# Remove all build output
clean: clean-source clean-tests
    @echo "Clean complete."

# Clean source and sample projects (all projects in solution)
clean-source:
    dotnet clean {{solution}} --configuration {{configuration}} --nologo -v q

# Clean test output directories
clean-tests:
    dotnet clean {{unit_tests_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{integration_tests_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{unit_tests_ai_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{integration_tests_ai_dir}} --configuration {{configuration}} --nologo -v q

# Restore NuGet packages
restore:
    dotnet restore {{solution}}

# Build in Release (default)
build: restore
    dotnet build {{solution}} --configuration {{configuration}} --no-restore

# Build in Debug
build-debug: restore
    dotnet build {{solution}} --configuration Debug --no-restore

# Run unit tests only — Agents library (no Temporal server required)
test-unit: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run unit tests only — AI library (no Temporal server required)
test-unit-ai: build
    dotnet test {{unit_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run all unit tests (Agents + AI)
test-unit-all: test-unit test-unit-ai

# Run integration tests only — Agents library (requires: temporal server start-dev)
test-integration: build
    @echo "NOTE: Integration tests require a running Temporal server."
    @echo "      Start one with: temporal server start-dev --namespace default"
    dotnet test {{integration_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run integration tests only — AI library (uses in-process test server)
test-integration-ai: build
    dotnet test {{integration_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run both unit and integration tests (all libraries)
test: test-unit-all test-integration test-integration-ai

# Run all tests (unit + integration) with code coverage — Agents and AI libraries
test-coverage: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/agents \
        --logger "console;verbosity=normal"
    dotnet test {{unit_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/ai \
        --logger "console;verbosity=normal"
    dotnet test {{integration_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/agents-integration \
        --logger "console;verbosity=normal"
    dotnet test {{integration_tests_ai_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{coverage_dir}}/ai-integration \
        --logger "console;verbosity=normal"

# Merge all coverage XML files into an HTML report and print line/branch summary
coverage-report: test-coverage
    dotnet tool run reportgenerator \
        -reports:"{{coverage_dir}}/**/*.cobertura.xml" \
        -targetdir:"{{coverage_dir}}/report" \
        -reporttypes:"HtmlInline_AzurePipelines;Cobertura;TextSummary"
    @cat "{{coverage_dir}}/report/Summary.txt"

# Run tests matching a filter expression (e.g. just test-filter "FullyQualifiedName~Router")
test-filter filter: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --filter "{{filter}}" \
        --logger "console;verbosity=normal"

# Pack NuGet packages (Release, into artifacts/packages/)
pack: clean build
    @echo "Creating NuGet packages with version: {{ version }}"
    @dotnet pack {{solution}} \
        --configuration {{configuration}} \
        --no-build \
        --output {{artifacts_dir}}
    @echo "Packages written to {{artifacts_dir}}/"

# Publish to NuGet.org (requires NUGET_API_KEY env var)
publish-nuget: pack
    @echo "Publishing to NuGet.org..."
    @if [ -z "$${NUGET_API_KEY:-}" ]; then \
        echo "❌ NUGET_API_KEY environment variable is not set"; \
        exit 1; \
    fi
    @dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --api-key "$NUGET_API_KEY" \
        --source "https://api.nuget.org/v3/index.json" \
        --skip-duplicate
    @echo "✓ Packages published to NuGet.org"

# Publish to GitHub Packages (requires NUGET_GITHUB_TOKEN env var)
publish-github: pack
    @echo "Publishing to GitHub Package Registry..."
    @if [ -z "$${NUGET_GITHUB_TOKEN:-}" ]; then \
        echo "❌ NUGET_GITHUB_TOKEN environment variable is not set"; \
        exit 1; \
    fi
    @dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --api-key "$NUGET_GITHUB_TOKEN" \
        --source "https://nuget.pkg.github.com/cecilphillip/index.json" \
        --skip-duplicate
    @echo "✓ Packages published to GitHub"

# Alias: build
compile: build

# Alias: test-unit (Agents only, for backward compatibility)
verify: test-unit

# Build + all unit tests (no server required)
validate: build test-unit-all

# Full local CI pipeline: clean → build → test-unit-all → pack
ci: clean build test-unit-all pack
