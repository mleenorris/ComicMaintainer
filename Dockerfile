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

# Copy watcher and process script
COPY watcher.py /watcher.py
COPY process_file.py /process_file.py
COPY web_app.py /web_app.py
COPY config.py /config.py
COPY version.py /version.py
COPY templates /templates
COPY start.sh /start.sh
COPY entrypoint.sh /entrypoint.sh

# Make scripts executable
RUN chmod +x /start.sh /entrypoint.sh

# Set default watched directory and script
ENV PROCESS_SCRIPT=/process_file.py
ENV WEB_PORT=5000
ENV PUID=99
ENV PGID=100

# Expose web interface port
EXPOSE 5000

# Use entrypoint to handle user switching
ENTRYPOINT ["/entrypoint.sh"]
CMD ["/start.sh"]
