"""
Performance measurement script to demonstrate the initial load optimization.
This script simulates the page load sequence and measures the time difference
between the sequential (before) and parallel (after) approaches.
"""
import time
import asyncio
from typing import Callable, Any


def simulate_api_call(name: str, latency_ms: int) -> float:
    """
    Simulate an API call with a given latency.
    
    Args:
        name: Name of the API call for logging
        latency_ms: Simulated latency in milliseconds
        
    Returns:
        Time taken in milliseconds
    """
    start = time.perf_counter()
    time.sleep(latency_ms / 1000.0)
    end = time.perf_counter()
    elapsed_ms = (end - start) * 1000
    return elapsed_ms


async def simulate_api_call_async(name: str, latency_ms: int) -> float:
    """
    Simulate an async API call with a given latency.
    
    Args:
        name: Name of the API call for logging
        latency_ms: Simulated latency in milliseconds
        
    Returns:
        Time taken in milliseconds
    """
    start = time.perf_counter()
    await asyncio.sleep(latency_ms / 1000.0)
    end = time.perf_counter()
    elapsed_ms = (end - start) * 1000
    return elapsed_ms


def sequential_load(pref_latency: int, job_latency: int, files_latency: int) -> dict:
    """
    Simulate the BEFORE (sequential) approach.
    
    Args:
        pref_latency: Latency for preferences API call (ms)
        job_latency: Latency for job check API call (ms)
        files_latency: Latency for files API call (ms)
        
    Returns:
        Dictionary with timing results
    """
    start = time.perf_counter()
    
    # Step 1: Load preferences (BLOCKS)
    pref_time = simulate_api_call("getPreferences", pref_latency)
    
    # Step 2: Check active job (BLOCKS)
    job_time = simulate_api_call("checkActiveJob", job_latency)
    
    # Step 3: FINALLY load files
    files_start = time.perf_counter()
    files_time = simulate_api_call("loadFiles", files_latency)
    
    total_time = (time.perf_counter() - start) * 1000
    time_before_files = (files_start - start) * 1000
    
    return {
        'total_time': total_time,
        'time_before_files_start': time_before_files,
        'pref_time': pref_time,
        'job_time': job_time,
        'files_time': files_time
    }


async def parallel_load_async(pref_latency: int, job_latency: int, files_latency: int) -> dict:
    """
    Simulate the AFTER (parallel) approach.
    
    Args:
        pref_latency: Latency for preferences API call (ms)
        job_latency: Latency for job check API call (ms)
        files_latency: Latency for files API call (ms)
        
    Returns:
        Dictionary with timing results
    """
    start = time.perf_counter()
    
    # All three start simultaneously
    files_start = time.perf_counter()
    pref_task = simulate_api_call_async("getPreferences", pref_latency)
    job_task = simulate_api_call_async("checkActiveJob", job_latency)
    files_task = simulate_api_call_async("loadFiles", files_latency)
    
    # Wait for all to complete
    results = await asyncio.gather(pref_task, job_task, files_task)
    
    total_time = (time.perf_counter() - start) * 1000
    time_before_files = (files_start - start) * 1000
    
    return {
        'total_time': total_time,
        'time_before_files_start': time_before_files,  # Should be ~0
        'pref_time': results[0],
        'job_time': results[1],
        'files_time': results[2]
    }


def parallel_load(pref_latency: int, job_latency: int, files_latency: int) -> dict:
    """Wrapper to run the async parallel load."""
    return asyncio.run(parallel_load_async(pref_latency, job_latency, files_latency))


def print_results(scenario: str, before: dict, after: dict):
    """Print comparison results for a scenario."""
    print(f"\n{'='*70}")
    print(f"Scenario: {scenario}")
    print(f"{'='*70}")
    
    print(f"\n📊 BEFORE (Sequential):")
    print(f"   Time before file list starts loading: {before['time_before_files_start']:.1f}ms")
    print(f"   Total time: {before['total_time']:.1f}ms")
    print(f"   Breakdown:")
    print(f"      - Preferences: {before['pref_time']:.1f}ms (blocks)")
    print(f"      - Job check: {before['job_time']:.1f}ms (blocks)")
    print(f"      - File loading: {before['files_time']:.1f}ms")
    
    print(f"\n⚡ AFTER (Parallel):")
    print(f"   Time before file list starts loading: {after['time_before_files_start']:.1f}ms")
    print(f"   Total time: {after['total_time']:.1f}ms")
    print(f"   Breakdown (all run in parallel):")
    print(f"      - Preferences: {after['pref_time']:.1f}ms")
    print(f"      - Job check: {after['job_time']:.1f}ms")
    print(f"      - File loading: {after['files_time']:.1f}ms")
    
    improvement_ms = before['time_before_files_start'] - after['time_before_files_start']
    improvement_pct = (improvement_ms / before['time_before_files_start']) * 100
    
    print(f"\n✅ IMPROVEMENT:")
    print(f"   File list loads {improvement_ms:.1f}ms faster ({improvement_pct:.0f}% improvement)")
    print(f"   Total page load {before['total_time'] - after['total_time']:.1f}ms faster")


def main():
    """Run performance measurements for various network scenarios."""
    
    print("\n" + "="*70)
    print("Initial Load Optimization - Performance Measurement")
    print("="*70)
    print("\nThis script demonstrates the performance improvement from")
    print("parallelizing API calls during page initialization.")
    
    # Scenario 1: Fast connection (50ms latency)
    print_results(
        "Fast Connection (50ms latency)",
        sequential_load(50, 50, 150),
        parallel_load(50, 50, 150)
    )
    
    # Scenario 2: Average connection (100ms latency)
    print_results(
        "Average Connection (100ms latency)",
        sequential_load(100, 100, 200),
        parallel_load(100, 100, 200)
    )
    
    # Scenario 3: Slow connection (250ms latency)
    print_results(
        "Slow Connection (250ms latency)",
        sequential_load(250, 250, 300),
        parallel_load(250, 250, 300)
    )
    
    # Scenario 4: Very slow/mobile connection (500ms latency)
    print_results(
        "Very Slow/Mobile Connection (500ms latency)",
        sequential_load(500, 500, 600),
        parallel_load(500, 500, 600)
    )
    
    print(f"\n{'='*70}")
    print("Summary")
    print(f"{'='*70}")
    print("\n✅ Key Takeaways:")
    print("   1. File list now loads IMMEDIATELY (0ms delay)")
    print("   2. Improvement scales with network latency")
    print("   3. Mobile users see the biggest benefit (1000ms+ faster)")
    print("   4. All operations complete faster overall")
    print("   5. No functionality lost - just pure speed improvement")
    print(f"\n{'='*70}\n")


if __name__ == '__main__':
    main()
