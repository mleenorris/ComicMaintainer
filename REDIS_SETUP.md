# Redis Backend Setup Guide

This guide explains how to set up ComicMaintainer with Redis backend for multi-worker deployments.

## Why Use Redis Backend?

The Redis backend enables:
- **Multi-worker support**: Run multiple Gunicorn workers for higher throughput
- **Job persistence**: Jobs survive container restarts
- **Horizontal scaling**: Deploy multiple instances sharing the same job state
- **High availability**: Load balance across multiple workers

## Quick Start with Docker Compose

### 1. Copy the example configuration

```bash
cp docker-compose.example.yml docker-compose.yml
```

### 2. Edit the configuration

Update the volume paths in `docker-compose.yml`:

```yaml
volumes:
  - /path/to/your/comics:/watched_dir
  - /path/to/duplicates:/duplicates
  - /path/to/config:/config
```

### 3. Start the services

```bash
docker-compose up -d
```

This will start:
- Redis container with persistent storage
- ComicMaintainer container configured to use Redis

### 4. (Optional) Enable Multi-Worker Mode

By default, the container runs with 1 Gunicorn worker. To enable multiple workers:

**Option A: Override start.sh**

Create a custom `start.sh` with multiple workers:

```bash
#!/bin/bash
cd /app
python /app/watcher.py &
WATCHER_PID=$!

WEB_PORT=${WEB_PORT:-5000}

# Use 4 workers for better performance
gunicorn --workers 4 --bind 0.0.0.0:${WEB_PORT} --timeout 600 web_app:app &
WEB_PID=$!

wait $WATCHER_PID $WEB_PID
```

Mount this in docker-compose.yml:

```yaml
volumes:
  - ./start.sh:/start.sh:ro
```

**Option B: Build custom image**

Create a Dockerfile that modifies start.sh:

```dockerfile
FROM iceburn1/comictagger-watcher:latest
COPY custom-start.sh /start.sh
RUN chmod +x /start.sh
```

## Standalone Docker Setup

If not using Docker Compose, run Redis separately and connect to it:

### 1. Start Redis

```bash
docker run -d \
  --name comicmaintainer-redis \
  -v redis-data:/data \
  redis:7-alpine redis-server --appendonly yes
```

### 2. Start ComicMaintainer

```bash
docker run -d \
  --name comicmaintainer \
  --link comicmaintainer-redis:redis \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/config \
  -e WATCHED_DIR=/watched_dir \
  -e CACHE_DIR=/config \
  -e JOB_STORE_BACKEND=redis \
  -e REDIS_URL=redis://redis:6379/0 \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

## Using External Redis

To use an external Redis service (e.g., AWS ElastiCache, Redis Cloud):

```bash
docker run -d \
  -e JOB_STORE_BACKEND=redis \
  -e REDIS_URL=redis://your-redis-host:6379/0 \
  # ... other environment variables ...
  iceburn1/comictagger-watcher:latest
```

## Configuration Options

### Environment Variables

- `JOB_STORE_BACKEND`: Set to `redis` to enable Redis backend (default: `memory`)
- `REDIS_URL`: Redis connection URL (default: `redis://localhost:6379/0`)
  - Format: `redis://[password@]host:port/database`
  - Example with auth: `redis://mypassword@redis-host:6379/0`

### Redis URL Formats

```
# Basic connection
redis://localhost:6379/0

# With password
redis://:mypassword@localhost:6379/0

# With username and password (Redis 6+)
redis://username:password@localhost:6379/0

# Redis Sentinel
redis+sentinel://sentinel1:26379,sentinel2:26379/mymaster/0

# Redis Cluster
redis://cluster-node1:7000,cluster-node2:7000/0
```

## Monitoring

### Check Redis Connection

```bash
# Exec into the container
docker exec -it comicmaintainer /bin/bash

# Check logs for Redis connection
tail -f /app/ComicMaintainer.log | grep -i redis
```

You should see:
```
INFO:root:Connected to Redis at redis://redis:6379/0
INFO:root:Using Redis backend for job storage at redis://redis:6379/0
```

### Check Job State in Redis

```bash
# Connect to Redis container
docker exec -it comicmaintainer-redis redis-cli

# List all job keys
KEYS comicmaintainer:job:*

# View a specific job
GET comicmaintainer:job:<job-id>

# Check TTL (Time To Live)
TTL comicmaintainer:job:<job-id>
```

## Troubleshooting

### Connection Refused

**Problem**: `Failed to connect to Redis: Error 111 connecting to localhost:6379`

**Solutions**:
1. Check Redis container is running: `docker ps | grep redis`
2. Verify network connectivity: `docker network inspect comicmaintainer`
3. Check Redis URL is correct: `echo $REDIS_URL`

### Jobs Not Persisting

**Problem**: Jobs disappear after container restart

**Solutions**:
1. Ensure Redis has persistent storage: `redis-server --appendonly yes`
2. Check Redis volume is mounted: `docker volume inspect redis-data`
3. Verify TTL is appropriate (default: 24 hours)

### Falling Back to In-Memory

**Problem**: `Failed to create redis job store, falling back to in-memory`

**Solutions**:
1. Check Redis package is installed: `pip list | grep redis`
2. Verify Redis URL format is correct
3. Ensure Redis container is accessible

## Performance Tuning

### Redis Configuration

For production deployments, tune Redis for better performance:

```bash
# In docker-compose.yml
services:
  redis:
    command: >
      redis-server
      --appendonly yes
      --maxmemory 512mb
      --maxmemory-policy allkeys-lru
      --save 900 1
      --save 300 10
```

### Worker Count

Adjust worker count based on your workload:
- **2 workers**: Light workload, low resource usage
- **4 workers**: Recommended for most deployments
- **8+ workers**: High-traffic deployments with many concurrent jobs

### Job TTL

Jobs automatically expire after 24 hours in Redis. To adjust, modify `job_store.py`:

```python
# Change from 86400 (24 hours) to desired TTL in seconds
self.redis_client.setex(key, 43200, serialized)  # 12 hours
```

## Migration from In-Memory

To migrate from in-memory to Redis backend:

1. **Stop the container**
2. **Update environment variables** (add `JOB_STORE_BACKEND=redis` and `REDIS_URL`)
3. **Start Redis** (if not already running)
4. **Start the container**

**Note**: Existing jobs will be lost during migration as they were stored in memory only.

## Best Practices

1. **Use persistent volumes** for Redis data
2. **Set up Redis authentication** for production
3. **Monitor Redis memory usage**
4. **Use Redis Sentinel** for high availability
5. **Back up Redis data** regularly
6. **Set appropriate job TTL** based on your needs
7. **Monitor job completion rates** to tune worker count

## Security Considerations

1. **Enable Redis AUTH**: Add password to `REDIS_URL`
2. **Use private networks**: Don't expose Redis port publicly
3. **Enable TLS**: Use `rediss://` (Redis with TLS) for sensitive data
4. **Set Redis ACLs**: Limit permissions (Redis 6+)

Example with authentication:
```yaml
environment:
  - REDIS_URL=redis://:strongpassword@redis:6379/0
```

## Additional Resources

- [Redis Documentation](https://redis.io/documentation)
- [Redis Python Client](https://redis-py.readthedocs.io/)
- [Docker Compose Documentation](https://docs.docker.com/compose/)
- [Gunicorn Workers](https://docs.gunicorn.org/en/stable/design.html#how-many-workers)
