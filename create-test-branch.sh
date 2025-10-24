#!/bin/bash
# Script to create the test branch from the end of PR 338
# Make this script executable with: chmod +x create-test-branch.sh

set -e  # Exit on error

# The end of PR 338 is at commit 57821b3fb90a52afb44652c4ca62b0b39f89fe07
# This commit merged the automatic version bumping feature

TARGET_COMMIT="57821b3fb90a52afb44652c4ca62b0b39f89fe07"
BRANCH_NAME="test"

echo "Creating test branch from end of PR 338 (commit: $TARGET_COMMIT)..."

# Check if the commit exists
if ! git cat-file -e "$TARGET_COMMIT" 2>/dev/null; then
    echo "Error: Commit $TARGET_COMMIT not found. Please fetch all commits first:"
    echo "  git fetch --unshallow"
    exit 1
fi

# Check if the branch already exists
if git rev-parse --verify "$BRANCH_NAME" >/dev/null 2>&1; then
    echo "Warning: Branch '$BRANCH_NAME' already exists locally."
    read -p "Do you want to delete and recreate it? (y/N) " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        git branch -D "$BRANCH_NAME"
    else
        echo "Aborting."
        exit 1
    fi
fi

git checkout -b "$BRANCH_NAME" "$TARGET_COMMIT"

echo "Pushing test branch to remote..."
if ! git push -u origin "$BRANCH_NAME"; then
    echo "Warning: Push failed. The branch may already exist on the remote."
    echo "To force push (use with caution): git push -f -u origin $BRANCH_NAME"
    exit 1
fi

echo "Test branch created and pushed successfully!"
echo "The test branch will be built and tagged as 'test' by the Docker workflow."

