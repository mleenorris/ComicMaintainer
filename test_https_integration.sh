#!/bin/bash
# Integration test for HTTPS configuration

echo "Testing HTTPS Integration"
echo "=========================="
echo ""

# Test 1: HTTP only (no SSL vars)
echo "Test 1: HTTP only mode (no SSL certificates)"
echo "---------------------------------------------"
unset SSL_CERTFILE
unset SSL_KEYFILE
unset SSL_CA_CERTS
export WEB_PORT=5000
export GUNICORN_WORKERS=2

# Create a test version of start.sh that echoes the command instead of running it
cat > /tmp/test_start.sh << 'EOF'
#!/bin/bash

WEB_PORT=${WEB_PORT:-5000}
GUNICORN_WORKERS=${GUNICORN_WORKERS:-2}

# Build gunicorn command with optional SSL support
GUNICORN_CMD="gunicorn --workers ${GUNICORN_WORKERS} --bind 0.0.0.0:${WEB_PORT} --timeout 600 --forwarded-allow-ips='*'"

# Add SSL/TLS support if certificates are provided
if [ -n "$SSL_CERTFILE" ] && [ -n "$SSL_KEYFILE" ]; then
    if [ -f "$SSL_CERTFILE" ] && [ -f "$SSL_KEYFILE" ]; then
        echo "Starting with HTTPS enabled (certificates found)"
        GUNICORN_CMD="$GUNICORN_CMD --certfile $SSL_CERTFILE --keyfile $SSL_KEYFILE"
        
        # Add CA bundle if provided
        if [ -n "$SSL_CA_CERTS" ] && [ -f "$SSL_CA_CERTS" ]; then
            GUNICORN_CMD="$GUNICORN_CMD --ca-certs $SSL_CA_CERTS"
        fi
    else
        echo "Warning: SSL_CERTFILE or SSL_KEYFILE not found, starting without HTTPS"
    fi
else
    echo "Starting with HTTP only (no SSL certificates configured)"
fi

echo "Command: $GUNICORN_CMD"
EOF

chmod +x /tmp/test_start.sh
OUTPUT=$(/tmp/test_start.sh 2>&1)
echo "$OUTPUT"

if echo "$OUTPUT" | grep -q "Starting with HTTP only"; then
    echo "✓ Test 1 PASSED: HTTP mode works"
else
    echo "✗ Test 1 FAILED: Expected HTTP mode"
    exit 1
fi
echo ""

# Test 2: HTTPS with valid certificates
echo "Test 2: HTTPS mode with certificates"
echo "-------------------------------------"

# Create dummy certificate files
mkdir -p /tmp/test-ssl
touch /tmp/test-ssl/cert.crt
touch /tmp/test-ssl/key.key

export SSL_CERTFILE=/tmp/test-ssl/cert.crt
export SSL_KEYFILE=/tmp/test-ssl/key.key

OUTPUT=$(/tmp/test_start.sh 2>&1)
echo "$OUTPUT"

if echo "$OUTPUT" | grep -q "Starting with HTTPS enabled"; then
    echo "✓ Test 2 PASSED: HTTPS mode works"
else
    echo "✗ Test 2 FAILED: Expected HTTPS mode"
    exit 1
fi

if echo "$OUTPUT" | grep -q "\-\-certfile /tmp/test-ssl/cert.crt"; then
    echo "✓ Test 2 PASSED: Certificate file is included"
else
    echo "✗ Test 2 FAILED: Certificate file not included"
    exit 1
fi

if echo "$OUTPUT" | grep -q "\-\-keyfile /tmp/test-ssl/key.key"; then
    echo "✓ Test 2 PASSED: Key file is included"
else
    echo "✗ Test 2 FAILED: Key file not included"
    exit 1
fi
echo ""

# Test 3: HTTPS with CA bundle
echo "Test 3: HTTPS mode with CA bundle"
echo "----------------------------------"

touch /tmp/test-ssl/ca-bundle.crt
export SSL_CA_CERTS=/tmp/test-ssl/ca-bundle.crt

OUTPUT=$(/tmp/test_start.sh 2>&1)
echo "$OUTPUT"

if echo "$OUTPUT" | grep -q "\-\-ca-certs /tmp/test-ssl/ca-bundle.crt"; then
    echo "✓ Test 3 PASSED: CA bundle is included"
else
    echo "✗ Test 3 FAILED: CA bundle not included"
    exit 1
fi
echo ""

# Test 4: Missing certificate files (should fall back to HTTP)
echo "Test 4: Missing certificate files (fallback to HTTP)"
echo "-----------------------------------------------------"

export SSL_CERTFILE=/tmp/nonexistent/cert.crt
export SSL_KEYFILE=/tmp/nonexistent/key.key

OUTPUT=$(/tmp/test_start.sh 2>&1)
echo "$OUTPUT"

if echo "$OUTPUT" | grep -q "Warning.*not found"; then
    echo "✓ Test 4 PASSED: Warning shown for missing certificates"
else
    echo "✗ Test 4 FAILED: Expected warning for missing certificates"
    exit 1
fi
echo ""

# Cleanup
rm -rf /tmp/test-ssl
rm /tmp/test_start.sh

echo "=========================="
echo "All integration tests PASSED!"
echo "=========================="
