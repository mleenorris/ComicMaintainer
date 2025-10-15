# Quick Start Guide - PostgreSQL Backend

## 🚀 Quick Start (Docker Compose)

### 1. Create `.env` file

```bash
cat > .env << EOF
WATCHED_DIR=/path/to/your/comics
DUPLICATE_DIR=/path/to/duplicates
CACHE_DIR=./cache
PUID=1000
PGID=1000
POSTGRES_PASSWORD=your_secure_password_here
EOF
```

### 2. Start Services

```bash
docker-compose up -d
```

### 3. Check Status

```bash
docker-compose ps
docker-compose logs -f app
```

### 4. Access Web Interface

Open your browser to: http://localhost:5000

## 🔧 Configuration

### Required Environment Variables

- `WATCHED_DIR` - Directory containing your comic files
- `DATABASE_URL` - PostgreSQL connection (auto-set in docker-compose)

### Optional Environment Variables

- `DUPLICATE_DIR` - Where duplicate files are moved
- `CACHE_DIR` - Persistent configuration storage (default: `./cache`)
- `WEB_PORT` - Web interface port (default: `5000`)
- `PUID` - User ID for file permissions (default: `99`)
- `PGID` - Group ID for file permissions (default: `100`)
- `GUNICORN_WORKERS` - Number of worker processes (default: `2`)
- `POSTGRES_PASSWORD` - Database password (required for docker-compose)

## 📊 Architecture

```
┌─────────────┐     ┌──────────────┐     ┌──────────────┐
│   Browser   │────▶│ Gunicorn (2x)│────▶│  PostgreSQL  │
└─────────────┘     └──────────────┘     └──────────────┘
                           │                     ▲
                           │                     │
                           └─────────────────────┘
                          Shared Job State
```

## 🔍 Troubleshooting

### Services won't start
```bash
# Check Docker Compose logs
docker-compose logs db
docker-compose logs app

# Verify .env file exists and has correct values
cat .env
```

### Can't connect to database
```bash
# Check database is running
docker-compose exec db pg_isready -U comicmaintainer

# Test connection from app
docker-compose exec app psql $DATABASE_URL -c "SELECT 1;"
```

### Jobs not persisting
```bash
# Verify DATABASE_URL is set correctly
docker-compose exec app env | grep DATABASE_URL

# Check database has jobs table
docker-compose exec db psql -U comicmaintainer -d comicmaintainer -c "\dt"
```

## 🛠️ Maintenance

### Backup Database

```bash
docker-compose exec db pg_dump -U comicmaintainer comicmaintainer > backup.sql
```

### Restore Database

```bash
cat backup.sql | docker-compose exec -T db psql -U comicmaintainer comicmaintainer
```

### View Job History

```bash
docker-compose exec db psql -U comicmaintainer -d comicmaintainer \
  -c "SELECT job_id, status, total_items, processed_items, created_at FROM jobs ORDER BY created_at DESC LIMIT 10;"
```

### Clean Old Jobs Manually

```bash
docker-compose exec db psql -U comicmaintainer -d comicmaintainer \
  -c "DELETE FROM jobs WHERE status IN ('completed', 'failed', 'cancelled') AND completed_at < EXTRACT(EPOCH FROM NOW()) - 3600;"
```

## 📚 Next Steps

- Read [MIGRATION_POSTGRESQL.md](MIGRATION_POSTGRESQL.md) for detailed migration guide
- Check [README.md](README.md) for full documentation
- See [ASYNC_PROCESSING.md](ASYNC_PROCESSING.md) for job system details

## ⚡ Performance Tuning

### Increase Workers

```yaml
# In docker-compose.yml
environment:
  GUNICORN_WORKERS: 4  # For systems with 4+ CPU cores
```

### Database Connection Pool

For high-load scenarios, consider using PgBouncer:
```yaml
# Add to docker-compose.yml
pgbouncer:
  image: pgbouncer/pgbouncer
  environment:
    DATABASES: comicmaintainer=host=db port=5432 dbname=comicmaintainer
```

## 🆘 Getting Help

1. Check logs: `docker-compose logs -f`
2. Review [MIGRATION_POSTGRESQL.md](MIGRATION_POSTGRESQL.md)
3. Open an issue on GitHub

## ✅ Success Indicators

Your setup is working correctly when:
- ✓ `docker-compose ps` shows both services as "Up"
- ✓ Web interface accessible at http://localhost:5000
- ✓ Jobs persist after `docker-compose restart app`
- ✓ Multiple concurrent requests work smoothly
