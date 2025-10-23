# Dockerfile for Python Watcher Service with ComicTagger
FROM python:3.11-slim
ENV PIP_USE_PEP517=true

# Install system dependencies for ComicTagger and gosu for user switching
RUN apt-get update && apt-get install -y \
    libqt5gui5 libqt5core5a libqt5widgets5 libqt5xml5 libicu-dev python3-pyqt5 pkg-config git g++ unrar-free make gosu && \
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

# Create config directory for persistent data
RUN mkdir -p /Config && chmod 755 /Config

# Set working directory
WORKDIR /app

# Copy all Python scripts to /app
COPY src/*.py /app/

# Copy templates directory
COPY templates /app/templates

# Copy shell scripts to root
COPY *.sh /

# Make scripts executable
RUN chmod +x /start.sh /entrypoint.sh

# Set default watched directory and script
ENV PROCESS_SCRIPT=/app/process_file.py
ENV WEB_PORT=5000
ENV PUID=99
ENV PGID=100
ENV MAX_WORKERS=4
ENV DB_CACHE_SIZE_MB=64

# Expose web interface port
EXPOSE 5000

# Use entrypoint to handle user switching
ENTRYPOINT ["/entrypoint.sh"]
CMD ["/start.sh"]
