import sys
import os
import re
import logging
from comicapi.comicarchive import ComicArchive
from config import get_filename_format
from markers import mark_file_duplicate, mark_file_processed

CONFIG_DIR = '/Config'
LOG_DIR = os.path.join(CONFIG_DIR, 'Log')

# Ensure log directory exists
os.makedirs(LOG_DIR, exist_ok=True)

# Set up logging to file and stdout (same as watcher.py)
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [WATCHER] %(levelname)s %(message)s',
    handlers=[
        logging.FileHandler(os.path.join(LOG_DIR, "ComicMaintainer.log")),
        logging.StreamHandler(sys.stdout)
    ]
)

CACHE_UPDATE_MARKER = '.cache_update'
CACHE_CHANGES_FILE = '.cache_changes'

def update_watcher_timestamp():
    """Update the watcher cache invalidation timestamp"""
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    marker_path = os.path.join(CONFIG_DIR, CACHE_UPDATE_MARKER)
    try:
        import time
        with open(marker_path, 'w') as f:
            f.write(str(time.time()))
    except Exception as e:
        logging.error(f"Error updating watcher timestamp: {e}")

def record_cache_change(change_type, old_path=None, new_path=None):
    """Record a file change for incremental cache updates
    
    Args:
        change_type: 'add', 'remove', or 'rename'
        old_path: Original file path (for 'remove' and 'rename')
        new_path: New file path (for 'add' and 'rename')
    """
    
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    changes_file = os.path.join(CONFIG_DIR, CACHE_CHANGES_FILE)
    
    try:
        import json
        import time
        
        change_entry = {
            'type': change_type,
            'old_path': old_path,
            'new_path': new_path,
            'timestamp': time.time()
        }
        
        # Append the change to the file
        with open(changes_file, 'a') as f:
            f.write(json.dumps(change_entry) + '\n')
        
        logging.info(f"Recorded cache change: {change_type} {old_path or ''} -> {new_path or ''}")
    except Exception as e:
        logging.error(f"Error recording cache change: {e}")

# mark_file_duplicate is now imported from markers module

def parse_chapter_number(filename):
    match = re.search(r'(?i)ch(?:apter)?[-._\s]*([0-9]+(?:\.[0-9]+)?)', filename)
    if match:
        return match.group(1)
    matches = list(re.finditer(r'(?<![\(\[])[0-9]+(?:\.[0-9]+)?(?![\)\]])', filename))
    for m in matches:
        start, end = m.start(), m.end()
        before = filename[:start]
        after = filename[end:]
        if (not re.search(r'[\(\[]$', before)) and (not re.search(r'^[\)\]]', after)):
            return m.group()
    return None

def format_filename(template, tags, issue_number):
    """
    Format filename based on template and available tags
    
    Supported placeholders:
    {series} - Series name
    {issue} - Issue number (padded to 4 digits)
    {issue_no_pad} - Issue number (no padding)
    {title} - Issue title
    {volume} - Volume number
    {year} - Publication year
    {publisher} - Publisher name
    """
    # Parse issue number into integer and decimal parts
    try:
        issue_str = str(issue_number)
        issue_float = float(issue_str)
        integer = int(issue_float)
        issue_padded = f"{integer:04d}"
        
        # Check if there's a decimal part
        if '.' in issue_str:
            # Extract decimal part from string and strip trailing zeros
            decimal_part = issue_str.split('.')[1].rstrip('0')
            if decimal_part:
                issue_formatted = f"{issue_padded}.{decimal_part}"
                issue_no_pad = f"{integer}.{decimal_part}"
            else:
                issue_formatted = issue_padded
                issue_no_pad = str(integer)
        else:
            issue_formatted = issue_padded
            issue_no_pad = str(integer)
    except:
        issue_formatted = str(issue_number)
        issue_no_pad = str(issue_number)
    
    # Build replacement dictionary
    replacements = {
        'series': tags.series or '',
        'issue': issue_formatted,
        'issue_no_pad': issue_no_pad,
        'title': tags.title or '',
        'volume': str(tags.volume) if tags.volume else '',
        'year': str(tags.year) if tags.year else '',
        'publisher': tags.publisher or ''
    }
    
    # Replace placeholders
    result = template
    for key, value in replacements.items():
        result = result.replace(f'{{{key}}}', str(value))
    
    # Clean up any remaining unreplaced placeholders
    result = re.sub(r'\{[^}]+\}', '', result)
    
    # Clean up extra spaces and ensure proper extension
    result = re.sub(r'\s+', ' ', result).strip()
    
    # Ensure .cbz extension
    if not result.lower().endswith('.cbz'):
        result += '.cbz'
    
    return result

def is_file_already_normalized(filepath, fixtitle=True, fixseries=True, fixfilename=True, comicfolder=None):
    """
    Check if a file is already normalized (metadata and filename match expected format).
    Returns True if the file doesn't need any changes.
    """
    try:
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        
        # Check title normalization if requested
        if fixtitle:
            issue_number = None
            try:
                if tags.issue:
                    issue_number = tags.issue
            except:
                pass
            
            if not issue_number:
                issue_number = parse_chapter_number(os.path.basename(filepath))
            
            if issue_number:
                expected_title = f"Chapter {issue_number}"
                if tags.title != expected_title:
                    return False
            else:
                # Can't determine issue number, so can't verify title
                return False
        
        # Check series normalization if requested
        if fixseries:
            if not comicfolder:
                comicfolder = os.path.dirname(filepath)
            seriesname = os.path.basename(comicfolder)
            seriesname = seriesname.replace('_', ':')
            seriesnamecompare = seriesname.replace("'", "\u0027")
            seriesnamecompare = re.sub(r"\(\*\)|\[\*\]", "", seriesnamecompare)
            
            series_name_tag = tags.series
            if series_name_tag:
                tags_series_compare = re.sub(r"\(\*\)|\[\*\]", "", series_name_tag)
                if tags_series_compare.strip() != seriesnamecompare.strip():
                    return False
            else:
                # No series tag, needs to be set
                return False
        
        # Check filename normalization if requested
        if fixfilename:
            if not tags.issue:
                # Can't format filename without issue number
                return False
                
            filename_template = get_filename_format()
            expected_filename = format_filename(filename_template, tags, tags.issue or '')
            current_filename = os.path.basename(filepath)
            
            if current_filename != expected_filename:
                return False
        
        return True
    except Exception as e:
        logging.error(f"Error checking if file is normalized: {e}")
        return False

def process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True, comicfolder=None):
    logging.info(f"Processing file: {filepath}")
    
    # Check if file is already normalized
    if is_file_already_normalized(filepath, fixtitle=fixtitle, fixseries=fixseries, fixfilename=fixfilename, comicfolder=comicfolder):
        logging.info(f"File {os.path.basename(filepath)} is already normalized. Skipping processing.")
        return filepath
    
    ca = ComicArchive(filepath)

    tags = ca.read_tags('cr')
    tagschanged = False

    # Title and issue logic
    if fixtitle:
        issue_number = None
        try:
            if tags.issue:
                issue_number = tags.issue
        except:
            logging.info(f"No issue tag found for {os.path.basename(filepath)}, will attempt to parse from filename...")
        if issue_number:
            logging.info(f"Issue number: {issue_number}")
            title = tags.title
            logging.info(f"Current title: {title}")
            if title == f"Chapter {issue_number}":
                logging.info(f"Already tagged title as Chapter {issue_number}, skipping {os.path.basename(filepath)}...")
            else:
                logging.info(f"Updating title to: Chapter {issue_number}")
                tags.title = f"Chapter {issue_number}"
                tagschanged = True
        else:
            issue_number = parse_chapter_number(os.path.basename(filepath))
            if issue_number:
                logging.info(f"Parsed chapter number: {issue_number}")
                title = tags.title
            
                if title == f"Chapter {issue_number}":
                    logging.info(f"Already tagged title as Chapter {issue_number}, skipping {os.path.basename(filepath)}...")
                else:
                    tags.title = f"Chapter {issue_number}"
                tags.issue = issue_number
                tagschanged = True
            else:
                logging.info(f"Could not parse chapter number from filename for {os.path.basename(filepath)}. Skipping...")

    # Series logic
    if fixseries:
        if not comicfolder:
            comicfolder = os.path.dirname(filepath)
        seriesname = os.path.basename(comicfolder)
        seriesname = seriesname.replace('_', ':')
        logging.info(f"Series name: {seriesname}")
        seriesnamecompare = seriesname.replace("'", "\u0027")
        seriesnamecompare = re.sub(r"\(\*\)|\[\*\]", "", seriesnamecompare)
        series_name_tag = tags.series
        if(series_name_tag):
            tags_series_compare = re.sub(r"\(\*\)|\[\*\]", "", series_name_tag if series_name_tag else "")
            if tags_series_compare.strip() == seriesnamecompare.strip():
                logging.info(f"Series name already correct for {os.path.basename(filepath)}, skipping...")
            else:
                logging.info(f"Fixing series name to: {seriesname}")
                tags.series = seriesname
                tagschanged = True
        else:
            logging.info(f"Fixing series name to: {seriesname}")
            tags.series = seriesname
            tagschanged = True

    #write tags back to file
    if tagschanged:
        ca.write_tags(tags, 'cr')

    # Track the final filepath (may change if renamed)
    final_filepath = filepath

    # Filename logic
    if fixfilename:
        try:
            tags = ca.read_tags('cr')
            # Get filename format template
            filename_template = get_filename_format()
            
            # Format the new filename
            newFileName = format_filename(filename_template, tags, tags.issue or '')
            
            newFilePath = os.path.join(os.path.dirname(filepath), newFileName)
            if os.path.abspath(filepath) != os.path.abspath(newFilePath):
                if os.path.exists(newFilePath):
                    # Mark the file as a duplicate
                    mark_file_duplicate(filepath)
                    
                    duplicate_dir = os.environ.get('DUPLICATE_DIR')
                    if duplicate_dir:
                        # Place under DUPLICATE_DIR/original_parent_folder/filename
                        original_parent = os.path.basename(os.path.dirname(filepath))
                        target_dir = os.path.join(duplicate_dir, original_parent)
                        os.makedirs(target_dir, exist_ok=True)
                        dest_path = os.path.join(target_dir, os.path.basename(filepath))
                        try:
                            logging.info(f"Duplicate detected. Moving {filepath} to {dest_path}")
                            #os.rename(filepath, dest_path)
                        except Exception as e:
                            logging.info(f"Error moving duplicate file {os.path.basename(filepath)}: {e}")
                    else:
                        logging.info(f"A file with the name {newFileName} already exists. Skipping rename for {os.path.basename(filepath)}. DUPLICATE_DIR not set.")
                else:
                    logging.info(f"Renaming file to: {newFileName}")
                    try:
                        os.rename(filepath, newFilePath)
                        final_filepath = newFilePath
                        # Record the rename for incremental cache update
                        record_cache_change('rename', old_path=filepath, new_path=newFilePath)
                    except Exception as e:
                        logging.info(f"Error renaming file {os.path.basename(filepath)}: {e}")
            else:
                logging.info(f"Filename already correct for {os.path.basename(filepath)}, skipping rename.")
        except Exception as e:
            logging.info(f"Could not format filename for {os.path.basename(filepath)}. Skipping rename... {e}")
    
    return final_filepath

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: process_file.py <filepath>")
        sys.exit(1)
    fixtitle = '--fixtitle' in sys.argv
    fixseries = '--fixseries' in sys.argv
    fixfilename = '--fixfilename' in sys.argv
    comicfolder = None
    for arg in sys.argv[2:]:
        if arg.startswith('--comicfolder='):
            comicfolder = arg.split('=', 1)[1]
    original_filepath = sys.argv[1]
    final_filepath = process_file(original_filepath, fixtitle=fixtitle or True, fixseries=fixseries or True, fixfilename=fixfilename or True, comicfolder=comicfolder)
    
    # Mark as processed using the final filepath (after any rename)
    mark_file_processed(final_filepath, original_filepath=original_filepath)
    
    # Update watcher timestamp to invalidate web app cache
    update_watcher_timestamp()