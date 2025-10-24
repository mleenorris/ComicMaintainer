#!/bin/bash
# Script to create the test branch from the end of PR 338

# The end of PR 338 is at commit 57821b3fb90a52afb44652c4ca62b0b39f89fe07
# This commit merged the automatic version bumping feature

echo "Creating test branch from end of PR 338..."
git checkout -b test 57821b3fb90a52afb44652c4ca62b0b39f89fe07

echo "Pushing test branch to remote..."
git push -u origin test

echo "Test branch created and pushed successfully!"
echo "The test branch will be built and tagged as 'test' by the Docker workflow."
