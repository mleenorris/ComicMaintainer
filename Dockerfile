# Dockerfile for Python Watcher Service with ComicTagger
FROM python:3.11-slim
ENV PIP_USE_PEP517=true

# Install system dependencies for ComicTagger, PostgreSQL client, and gosu for user switching
RUN apt-get update && apt-get install -y \
    libqt5gui5 libqt5core5a libqt5widgets5 libqt5xml5 libicu-dev python3-pyqt5 \
    pkg-config git g++ unrar-free make gosu \
    libpq-dev postgresql-client && \
    rm -rf /var/lib/apt/lists/*

# Install Python dependencies
RUN pip install --upgrade pip
COPY requirements.txt /requirements.txt
RUN pip install --no-cache-dir -r /requirements.txt

# Install ComicTagger from develop branch
RUN git clone --branch develop https://github.com/comictagger/comictagger.git /comictagger && \
    pip3 install /comictagger[CBR,ICU,7Z]

# Create app directory for the application
RUN mkdir -p /app && chmod 755 /app

# Create cache directory for server-side caching
RUN mkdir -p /app/cache && chmod 755 /app/cache

# Set working directory
WORKDIR /app

# Copy watcher and process script
COPY watcher.py /app/watcher.py
COPY process_file.py /app/process_file.py
COPY web_app.py /app/web_app.py
COPY config.py /app/config.py
COPY job_manager.py /app/job_manager.py
COPY job_db.py /app/job_db.py
COPY markers.py /app/markers.py
COPY version.py /app/version.py
COPY templates /app/templates
COPY start.sh /start.sh
COPY entrypoint.sh /entrypoint.sh

# Make scripts executable
RUN chmod +x /start.sh /entrypoint.sh

# Set default watched directory and script
ENV PROCESS_SCRIPT=/app/process_file.py
ENV WEB_PORT=5000
ENV CACHE_DIR=/app/cache
ENV PUID=99
ENV PGID=100

# Expose web interface port
EXPOSE 5000

# Use entrypoint to handle user switching
ENTRYPOINT ["/entrypoint.sh"]
CMD ["/start.sh"]
