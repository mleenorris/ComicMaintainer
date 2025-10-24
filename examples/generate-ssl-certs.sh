#!/bin/bash
#
# Generate self-signed SSL certificates for testing HTTPS
#
# Usage: ./generate-ssl-certs.sh [output_directory]
#
# This script generates a self-signed certificate valid for 365 days.
# For production use, please use certificates from a trusted CA like Let's Encrypt.
#

set -e

# Default output directory
OUTPUT_DIR="${1:-./ssl}"

# Create output directory
mkdir -p "$OUTPUT_DIR"

echo "Generating self-signed SSL certificate..."
echo "Output directory: $OUTPUT_DIR"
echo ""

# Generate certificate
openssl req -x509 -newkey rsa:4096 -nodes \
  -keyout "$OUTPUT_DIR/key.pem" \
  -out "$OUTPUT_DIR/cert.pem" \
  -days 365 \
  -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"

echo ""
echo "✓ Certificate generated successfully!"
echo ""
echo "Certificate file: $OUTPUT_DIR/cert.pem"
echo "Key file:         $OUTPUT_DIR/key.pem"
echo ""
echo "To use with Docker:"
echo "  docker run -d \\"
echo "    -v \$(pwd)/$OUTPUT_DIR:/ssl:ro \\"
echo "    -e HTTPS_ENABLED=true \\"
echo "    -e SSL_CERT=/ssl/cert.pem \\"
echo "    -e SSL_KEY=/ssl/key.pem \\"
echo "    -e WATCHED_DIR=/watched_dir \\"
echo "    -v /path/to/comics:/watched_dir \\"
echo "    -v /path/to/config:/Config \\"
echo "    -p 443:5000 \\"
echo "    iceburn1/comictagger-watcher:latest"
echo ""
echo "Access the web interface at: https://localhost:443"
echo ""
echo "Note: Your browser will show a security warning because this is a"
echo "self-signed certificate. This is normal for testing. For production,"
echo "use certificates from a trusted CA like Let's Encrypt."
echo ""

# Display certificate info
echo "Certificate details:"
openssl x509 -in "$OUTPUT_DIR/cert.pem" -text -noout | grep -A 2 "Subject:\|Validity" | head -6
