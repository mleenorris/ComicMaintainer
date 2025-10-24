#!/bin/bash

# Script to create the stable branch from PR #344 merge commit
# This script can be run manually if needed

set -e

echo "Creating stable branch from PR #344 merge commit..."

# The merge commit SHA from PR 344
PR_344_MERGE_COMMIT="da2b65cee64849458ded38203c6ca13b9267bf8a"

# Fetch all branches and commits
echo "Fetching latest changes from origin..."
git fetch origin

# Check if stable branch already exists on remote first
if git ls-remote --exit-code --heads origin stable >/dev/null 2>&1; then
    echo "Stable branch already exists on remote"
    # Check if local branch exists and sync it
    if git show-ref --verify --quiet refs/heads/stable; then
        echo "Local stable branch exists. Consider syncing with: git fetch origin stable:stable"
    else
        echo "Creating local stable branch from remote..."
        git branch stable origin/stable
    fi
    exit 0
fi

# Check if stable branch already exists locally
if git show-ref --verify --quiet refs/heads/stable; then
    echo "Stable branch already exists locally"
else
    # Create stable branch from PR 344 merge commit
    echo "Creating local stable branch from commit $PR_344_MERGE_COMMIT..."
    git branch stable "$PR_344_MERGE_COMMIT"
    echo "Local stable branch created successfully"
fi

# Push the stable branch to origin
echo "Pushing stable branch to origin..."
git push origin stable

echo "Successfully created and pushed stable branch!"
echo "Branch stable now points to: $PR_344_MERGE_COMMIT"
echo ""
echo "Verify with: git log stable -1"
