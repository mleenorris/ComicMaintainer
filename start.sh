#!/bin/bash

# Start the watcher in the background
python /watcher.py &
WATCHER_PID=$!

# Start the web app
python /web_app.py &
WEB_PID=$!

# Wait for both processes
wait $WATCHER_PID $WEB_PID
