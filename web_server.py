import os
import sys
import logging
import subprocess
import threading
from flask import Flask, render_template_string, jsonify
from pathlib import Path

WATCHED_DIR = os.environ.get('WATCHED_DIR')
PROCESS_SCRIPT = os.environ.get('PROCESS_SCRIPT', 'process_file.py')

app = Flask(__name__)

HTML_TEMPLATE = """
<!DOCTYPE html>
<html>
<head>
    <title>Comic Maintainer</title>
    <style>
        body {
            font-family: Arial, sans-serif;
            max-width: 1200px;
            margin: 50px auto;
            padding: 20px;
            background-color: #f5f5f5;
        }
        h1 {
            color: #333;
            text-align: center;
        }
        .container {
            background-color: white;
            padding: 30px;
            border-radius: 8px;
            box-shadow: 0 2px 4px rgba(0,0,0,0.1);
        }
        .info {
            background-color: #e3f2fd;
            padding: 15px;
            border-radius: 4px;
            margin-bottom: 20px;
        }
        button {
            background-color: #4CAF50;
            color: white;
            padding: 15px 32px;
            text-align: center;
            text-decoration: none;
            display: inline-block;
            font-size: 16px;
            margin: 4px 2px;
            cursor: pointer;
            border: none;
            border-radius: 4px;
            width: 100%;
        }
        button:hover {
            background-color: #45a049;
        }
        button:disabled {
            background-color: #cccccc;
            cursor: not-allowed;
        }
        #status {
            margin-top: 20px;
            padding: 15px;
            border-radius: 4px;
            display: none;
        }
        .success {
            background-color: #d4edda;
            border: 1px solid #c3e6cb;
            color: #155724;
        }
        .error {
            background-color: #f8d7da;
            border: 1px solid #f5c6cb;
            color: #721c24;
        }
        .processing {
            background-color: #fff3cd;
            border: 1px solid #ffeaa7;
            color: #856404;
        }
        .file-list {
            margin-top: 20px;
            max-height: 400px;
            overflow-y: auto;
            background-color: #f9f9f9;
            padding: 10px;
            border-radius: 4px;
        }
        .file-item {
            padding: 5px;
            margin: 2px 0;
            background-color: white;
            border-left: 3px solid #4CAF50;
            padding-left: 10px;
        }
        .stats {
            display: flex;
            justify-content: space-around;
            margin-top: 20px;
        }
        .stat-box {
            text-align: center;
            padding: 15px;
            background-color: #f0f0f0;
            border-radius: 4px;
            flex: 1;
            margin: 0 10px;
        }
        .stat-number {
            font-size: 2em;
            font-weight: bold;
            color: #4CAF50;
        }
        .stat-label {
            color: #666;
            margin-top: 5px;
        }
    </style>
</head>
<body>
    <h1>Comic Maintainer Control Panel</h1>
    <div class="container">
        <div class="info">
            <strong>Watched Directory:</strong> {{ watched_dir }}
        </div>
        
        <button id="processBtn" onclick="processAllFiles()">
            Process All Files in Watched Directory
        </button>
        
        <div id="status"></div>
        
        <div class="stats" id="stats" style="display: none;">
            <div class="stat-box">
                <div class="stat-number" id="totalFiles">0</div>
                <div class="stat-label">Total Files</div>
            </div>
            <div class="stat-box">
                <div class="stat-number" id="processedFiles">0</div>
                <div class="stat-label">Processed</div>
            </div>
        </div>
        
        <div class="file-list" id="fileList"></div>
    </div>

    <script>
        function updateStatus(message, type) {
            const statusDiv = document.getElementById('status');
            statusDiv.textContent = message;
            statusDiv.className = type;
            statusDiv.style.display = 'block';
        }

        function processAllFiles() {
            const btn = document.getElementById('processBtn');
            btn.disabled = true;
            updateStatus('Processing files... This may take a while.', 'processing');
            
            document.getElementById('fileList').innerHTML = '';
            document.getElementById('stats').style.display = 'none';
            
            fetch('/process-all', {
                method: 'POST'
            })
            .then(response => response.json())
            .then(data => {
                btn.disabled = false;
                if (data.success) {
                    updateStatus(data.message, 'success');
                    
                    // Show stats
                    document.getElementById('totalFiles').textContent = data.total_files;
                    document.getElementById('processedFiles').textContent = data.processed_files;
                    document.getElementById('stats').style.display = 'flex';
                    
                    // Show file list
                    if (data.files && data.files.length > 0) {
                        const fileList = document.getElementById('fileList');
                        data.files.forEach(file => {
                            const fileItem = document.createElement('div');
                            fileItem.className = 'file-item';
                            fileItem.textContent = file;
                            fileList.appendChild(fileItem);
                        });
                    }
                } else {
                    updateStatus('Error: ' + data.message, 'error');
                }
            })
            .catch(error => {
                btn.disabled = false;
                updateStatus('Error: ' + error.message, 'error');
            });
        }
    </script>
</body>
</html>
"""

@app.route('/')
def index():
    return render_template_string(HTML_TEMPLATE, watched_dir=WATCHED_DIR or "Not Set")

@app.route('/process-all', methods=['POST'])
def process_all():
    if not WATCHED_DIR:
        return jsonify({
            'success': False,
            'message': 'WATCHED_DIR environment variable is not set'
        })
    
    if not os.path.exists(WATCHED_DIR):
        return jsonify({
            'success': False,
            'message': f'Watched directory does not exist: {WATCHED_DIR}'
        })
    
    # Find all comic files
    comic_files = []
    for root, dirs, files in os.walk(WATCHED_DIR):
        for file in files:
            if file.lower().endswith(('.cbz', '.cbr')):
                comic_files.append(os.path.join(root, file))
    
    # Process each file
    processed_files = []
    for filepath in comic_files:
        try:
            logging.info(f"Web UI: Processing {filepath}")
            result = subprocess.run(
                [sys.executable, PROCESS_SCRIPT, filepath],
                capture_output=True,
                text=True,
                timeout=300  # 5 minute timeout per file
            )
            processed_files.append(os.path.basename(filepath))
        except subprocess.TimeoutExpired:
            logging.error(f"Timeout processing {filepath}")
        except Exception as e:
            logging.error(f"Error processing {filepath}: {e}")
    
    return jsonify({
        'success': True,
        'message': f'Processed {len(processed_files)} of {len(comic_files)} files',
        'total_files': len(comic_files),
        'processed_files': len(processed_files),
        'files': [os.path.basename(f) for f in comic_files]
    })

def run_server(host='0.0.0.0', port=5000):
    """Run the Flask web server"""
    logging.info(f"Starting web server on {host}:{port}")
    app.run(host=host, port=port, debug=False)

if __name__ == '__main__':
    run_server()
