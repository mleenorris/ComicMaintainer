#!/bin/bash
# Script to run unit tests with code coverage

set -e

echo "Running unit tests with code coverage..."

# Run tests with code coverage
dotnet test \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults \
  --logger "console;verbosity=detailed"

echo ""
echo "Test execution completed!"

# Find coverage files
COVERAGE_FILES=$(find ./TestResults -name "coverage.cobertura.xml" -type f)

if [ -z "$COVERAGE_FILES" ]; then
  echo "No coverage files found!"
  exit 1
fi

echo ""
echo "Coverage reports generated at:"
for FILE in $COVERAGE_FILES; do
  echo "  - $FILE"
done

# Check if reportgenerator is installed
if command -v reportgenerator &> /dev/null; then
  echo ""
  echo "Generating HTML coverage report..."
  
  reportgenerator \
    -reports:"./TestResults/**/coverage.cobertura.xml" \
    -targetdir:"./TestResults/CoverageReport" \
    -reporttypes:"Html;HtmlSummary;TextSummary"
  
  echo ""
  echo "HTML coverage report available at: ./TestResults/CoverageReport/index.html"
  
  # Display summary
  if [ -f "./TestResults/CoverageReport/Summary.txt" ]; then
    echo ""
    echo "Coverage Summary:"
    cat "./TestResults/CoverageReport/Summary.txt"
  fi
else
  echo ""
  echo "Note: Install reportgenerator for HTML coverage reports:"
  echo "  dotnet tool install -g dotnet-reportgenerator-globaltool"
fi

echo ""
echo "Done!"
