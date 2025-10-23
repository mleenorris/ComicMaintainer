import sys
import os
import re
import logging
from comicapi.comicarchive import ComicArchive
from config import get_filename_format, get_issue_number_padding
from markers import mark_file_duplicate, mark_file_processed
from error_handler import (
    setup_debug_logging, log_debug, log_error_with_context,
    log_function_entry, log_function_exit
)
import file_store
from unified_store import add_processing_history

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

# Setup debug logging if DEBUG_MODE is enabled
setup_debug_logging()
log_debug("process_file module initialized")

# Initialize file store
file_store.init_db()

def record_file_change(change_type, old_path=None, new_path=None):
    """Record a file change directly in the file store
    
    Args:
        change_type: 'add', 'remove', or 'rename'
        old_path: Original file path (for 'remove' and 'rename')
        new_path: New file path (for 'add' and 'rename')
    """
    log_function_entry("record_file_change", change_type=change_type, old_path=old_path, new_path=new_path)
    
    try:
        if change_type == 'add' and new_path:
            file_store.add_file(new_path)
            logging.info(f"Added file to store: {new_path}")
        elif change_type == 'remove' and old_path:
            file_store.remove_file(old_path)
            logging.info(f"Removed file from store: {old_path}")
        elif change_type == 'rename' and old_path and new_path:
            file_store.rename_file(old_path, new_path)
            logging.info(f"Renamed file in store: {old_path} -> {new_path}")
        
        log_function_exit("record_file_change", result="success")
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Recording file change in process_file: {change_type}",
            additional_info={"old_path": old_path, "new_path": new_path}
        )
        logging.error(f"Error recording file change: {e}")

# mark_file_duplicate is now imported from markers module

# Compile regex patterns once for better performance
_CHAPTER_KEYWORD_PATTERN = re.compile(r'(?i)ch(?:apter)?[-._\s]*([0-9]+(?:\.[0-9]+)?)')
_NUMBER_PATTERN = re.compile(r'(?<![\(\[])[0-9]+(?:\.[0-9]+)?(?![\)\]])')
_BRACKET_START_PATTERN = re.compile(r'[\(\[]$')
_BRACKET_END_PATTERN = re.compile(r'^[\)\]]')

def parse_chapter_number(filename):
    log_function_entry("parse_chapter_number", filename=filename)
    
    match = _CHAPTER_KEYWORD_PATTERN.search(filename)
    if match:
        chapter_num = match.group(1)
        log_debug("Found chapter number via chapter keyword", filename=filename, chapter=chapter_num)
        log_function_exit("parse_chapter_number", result=chapter_num)
        return chapter_num
    
    matches = list(_NUMBER_PATTERN.finditer(filename))
    log_debug("Searching for chapter number in filename", filename=filename, matches_count=len(matches))
    
    for m in matches:
        start, end = m.start(), m.end()
        before = filename[:start]
        after = filename[end:]
        if (not _BRACKET_START_PATTERN.search(before)) and (not _BRACKET_END_PATTERN.search(after)):
            chapter_num = m.group()
            log_debug("Found chapter number via number pattern", filename=filename, chapter=chapter_num)
            log_function_exit("parse_chapter_number", result=chapter_num)
            return chapter_num
    
    log_debug("No chapter number found in filename", filename=filename)
    log_function_exit("parse_chapter_number", result=None)
    return None

def format_filename(template, tags, issue_number, original_extension='.cbz'):
    """
    Format filename based on template and available tags
    
    Supported placeholders:
    {series} - Series name
    {issue} - Issue number (padded based on settings, default 4 digits)
    {issue_no_pad} - Issue number (no padding)
    {title} - Issue title
    {volume} - Volume number
    {year} - Publication year
    {publisher} - Publisher name
    
    Args:
        original_extension: The extension to use (.cbz or .cbr), preserves original file format
    """
    # Parse issue number into integer and decimal parts
    try:
        issue_str = str(issue_number)
        issue_float = float(issue_str)
        integer = int(issue_float)
        padding = get_issue_number_padding()
        issue_padded = f"{integer:0{padding}d}"
        
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
    
    # Ensure proper extension (preserve original format)
    if not (result.lower().endswith('.cbz') or result.lower().endswith('.cbr')):
        result += original_extension
    
    return result

def is_file_already_normalized(filepath, fixtitle=True, fixseries=True, fixfilename=True, comicfolder=None):
    """
    Check if a file is already normalized (metadata and filename match expected format).
    Returns True if the file doesn't need any changes.
    """
    log_function_entry("is_file_already_normalized", filepath=filepath, fixtitle=fixtitle, fixseries=fixseries, fixfilename=fixfilename)
    
    try:
        log_debug("Opening comic archive", filepath=filepath)
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        log_debug("Read tags from archive", filepath=filepath, has_tags=tags is not None)
        
        # Check title normalization if requested
        if fixtitle:
            log_debug("Checking title normalization", filepath=filepath)
            issue_number = None
            try:
                if tags.issue:
                    issue_number = tags.issue
                    log_debug("Found issue number in tags", issue_number=issue_number)
            except:
                pass
            
            if not issue_number:
                issue_number = parse_chapter_number(os.path.basename(filepath))
            
            if issue_number:
                expected_title = f"Chapter {issue_number}"
                log_debug("Checking title", current_title=tags.title, expected_title=expected_title)
                if tags.title != expected_title:
                    log_debug("Title mismatch, normalization needed", filepath=filepath)
                    log_function_exit("is_file_already_normalized", result=False)
                    return False
            else:
                # Can't determine issue number, so can't verify title
                log_debug("Cannot determine issue number", filepath=filepath)
                log_function_exit("is_file_already_normalized", result=False)
                return False
        
        # Check series normalization if requested
        if fixseries:
            log_debug("Checking series normalization", filepath=filepath)
            if not comicfolder:
                comicfolder = os.path.dirname(filepath)
            seriesname = os.path.basename(comicfolder)
            seriesname = seriesname.replace('_', ':')
            seriesnamecompare = seriesname.replace("'", "\u0027")
            seriesnamecompare = re.sub(r"\(\*\)|\[\*\]", "", seriesnamecompare)
            
            series_name_tag = tags.series
            log_debug("Checking series", current_series=series_name_tag, expected_series=seriesnamecompare)
            if series_name_tag:
                tags_series_compare = re.sub(r"\(\*\)|\[\*\]", "", series_name_tag)
                if tags_series_compare.strip() != seriesnamecompare.strip():
                    log_debug("Series mismatch, normalization needed", filepath=filepath)
                    log_function_exit("is_file_already_normalized", result=False)
                    return False
            else:
                # No series tag, needs to be set
                log_debug("No series tag found, normalization needed", filepath=filepath)
                log_function_exit("is_file_already_normalized", result=False)
                return False
        
        # Check filename normalization if requested
        if fixfilename:
            log_debug("Checking filename normalization", filepath=filepath)
            if not tags.issue:
                # Can't format filename without issue number
                log_debug("No issue tag for filename check", filepath=filepath)
                log_function_exit("is_file_already_normalized", result=False)
                return False
                
            # Get original file extension to preserve format
            original_ext = os.path.splitext(filepath)[1].lower()
            filename_template = get_filename_format()
            expected_filename = format_filename(filename_template, tags, tags.issue or '', original_extension=original_ext)
            current_filename = os.path.basename(filepath)
            
            log_debug("Checking filename", current=current_filename, expected=expected_filename)
            if current_filename != expected_filename:
                log_debug("Filename mismatch, normalization needed", filepath=filepath)
                log_function_exit("is_file_already_normalized", result=False)
                return False
        
        log_debug("File is already normalized", filepath=filepath)
        log_function_exit("is_file_already_normalized", result=True)
        return True
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Checking if file is normalized: {filepath}",
            additional_info={"filepath": filepath, "fixtitle": fixtitle, "fixseries": fixseries, "fixfilename": fixfilename}
        )
        logging.error(f"Error checking if file is normalized: {e}")
        return False

def process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True, comicfolder=None):
    log_function_entry("process_file", filepath=filepath, fixtitle=fixtitle, fixseries=fixseries, fixfilename=fixfilename)
    logging.info(f"Processing file: {filepath}")
    
    # Check if file is already normalized
    if is_file_already_normalized(filepath, fixtitle=fixtitle, fixseries=fixseries, fixfilename=fixfilename, comicfolder=comicfolder):
        logging.info(f"File {os.path.basename(filepath)} is already normalized. Skipping processing.")
        log_function_exit("process_file", result=filepath)
        return filepath
    
    log_debug("File needs normalization, proceeding with processing", filepath=filepath)
    
    # Capture "before" state for history tracking
    before_filename = os.path.basename(filepath)
    before_title = None
    before_series = None
    before_issue = None
    before_publisher = None
    before_year = None
    before_volume = None
    
    try:
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        tagschanged = False
        log_debug("Read comic tags", filepath=filepath, has_tags=tags is not None)
        
        # Capture before state
        if tags:
            before_title = tags.title
            before_series = tags.series
            before_issue = tags.issue
            before_publisher = tags.publisher
            before_year = str(tags.year) if tags.year else None
            before_volume = str(tags.volume) if tags.volume else None
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Opening comic archive: {filepath}",
            additional_info={"filepath": filepath}
        )
        raise

    # Title and issue logic
    if fixtitle:
        log_debug("Processing title and issue", filepath=filepath)
        issue_number = None
        try:
            if tags.issue:
                issue_number = tags.issue
                log_debug("Found issue tag", issue_number=issue_number)
        except Exception as e:
            log_debug("Error reading issue tag", error=str(e))
            logging.info(f"No issue tag found for {os.path.basename(filepath)}, will attempt to parse from filename...")
        
        if issue_number:
            logging.info(f"Issue number: {issue_number}")
            title = tags.title
            logging.info(f"Current title: {title}")
            log_debug("Checking title format", current_title=title, issue_number=issue_number)
            
            if title == f"Chapter {issue_number}":
                logging.info(f"Already tagged title as Chapter {issue_number}, skipping {os.path.basename(filepath)}...")
                log_debug("Title already correct", filepath=filepath)
            else:
                logging.info(f"Updating title to: Chapter {issue_number}")
                log_debug("Updating title", old_title=title, new_title=f"Chapter {issue_number}")
                tags.title = f"Chapter {issue_number}"
                tagschanged = True
        else:
            log_debug("No issue tag found, parsing from filename", filepath=filepath)
            issue_number = parse_chapter_number(os.path.basename(filepath))
            
            if issue_number:
                logging.info(f"Parsed chapter number: {issue_number}")
                log_debug("Successfully parsed chapter number", issue_number=issue_number)
                title = tags.title
            
                if title == f"Chapter {issue_number}":
                    logging.info(f"Already tagged title as Chapter {issue_number}, skipping {os.path.basename(filepath)}...")
                else:
                    log_debug("Setting title and issue", issue_number=issue_number)
                    tags.title = f"Chapter {issue_number}"
                    tags.issue = issue_number
                    tagschanged = True
            else:
                logging.info(f"Could not parse chapter number from filename for {os.path.basename(filepath)}. Skipping...")
                log_debug("Failed to parse chapter number", filepath=filepath)

    # Series logic
    if fixseries:
        log_debug("Processing series metadata", filepath=filepath)
        if not comicfolder:
            comicfolder = os.path.dirname(filepath)
        
        seriesname = os.path.basename(comicfolder)
        seriesname = seriesname.replace('_', ':')
        logging.info(f"Series name: {seriesname}")
        log_debug("Derived series name from folder", folder=comicfolder, series=seriesname)
        
        seriesnamecompare = seriesname.replace("'", "\u0027")
        seriesnamecompare = re.sub(r"\(\*\)|\[\*\]", "", seriesnamecompare)
        series_name_tag = tags.series
        
        log_debug("Checking series metadata", current_series=series_name_tag, expected_series=seriesnamecompare)
        
        if(series_name_tag):
            tags_series_compare = re.sub(r"\(\*\)|\[\*\]", "", series_name_tag if series_name_tag else "")
            if tags_series_compare.strip() == seriesnamecompare.strip():
                logging.info(f"Series name already correct for {os.path.basename(filepath)}, skipping...")
                log_debug("Series already correct", filepath=filepath)
            else:
                logging.info(f"Fixing series name to: {seriesname}")
                log_debug("Updating series", old_series=series_name_tag, new_series=seriesname)
                tags.series = seriesname
                tagschanged = True
        else:
            logging.info(f"Fixing series name to: {seriesname}")
            log_debug("Setting series (was empty)", new_series=seriesname)
            tags.series = seriesname
            tagschanged = True

    #write tags back to file
    if tagschanged:
        log_debug("Tags were changed, writing back to file", filepath=filepath)
        try:
            ca.write_tags(tags, 'cr')
            log_debug("Successfully wrote tags", filepath=filepath)
        except Exception as e:
            log_error_with_context(
                e,
                context=f"Writing tags to file: {filepath}",
                additional_info={"filepath": filepath, "tags_changed": tagschanged}
            )
            raise
    else:
        log_debug("No tag changes needed", filepath=filepath)

    # Track the final filepath (may change if renamed)
    final_filepath = filepath

    # Filename logic
    if fixfilename:
        log_debug("Processing filename", filepath=filepath)
        try:
            tags = ca.read_tags('cr')
            # Get filename format template
            filename_template = get_filename_format()
            log_debug("Got filename template", template=filename_template)
            
            # Get original file extension to preserve format
            original_ext = os.path.splitext(filepath)[1].lower()
            log_debug("Original file extension", extension=original_ext)
            
            # Format the new filename
            newFileName = format_filename(filename_template, tags, tags.issue or '', original_extension=original_ext)
            log_debug("Formatted new filename", old=os.path.basename(filepath), new=newFileName)
            
            newFilePath = os.path.join(os.path.dirname(filepath), newFileName)
            if os.path.abspath(filepath) != os.path.abspath(newFilePath):
                log_debug("File needs to be renamed", old_path=filepath, new_path=newFilePath)
                
                if os.path.exists(newFilePath):
                    log_debug("Target filename already exists - duplicate detected", target=newFilePath)
                    # Mark the file as a duplicate
                    mark_file_duplicate(filepath)
                    
                    duplicate_dir = os.environ.get('DUPLICATE_DIR')
                    if duplicate_dir:
                        # Place under DUPLICATE_DIR/original_parent_folder/filename
                        original_parent = os.path.basename(os.path.dirname(filepath))
                        target_dir = os.path.join(duplicate_dir, original_parent)
                        log_debug("Moving duplicate to duplicate directory", target_dir=target_dir)
                        
                        try:
                            os.makedirs(target_dir, exist_ok=True)
                            dest_path = os.path.join(target_dir, os.path.basename(filepath))
                            logging.info(f"Duplicate detected. Moving {filepath} to {dest_path}")
                            log_debug("Duplicate move destination", dest=dest_path)
                            #os.rename(filepath, dest_path)
                        except Exception as e:
                            log_error_with_context(
                                e,
                                context=f"Moving duplicate file: {filepath}",
                                additional_info={"filepath": filepath, "dest_path": dest_path}
                            )
                            logging.info(f"Error moving duplicate file {os.path.basename(filepath)}: {e}")
                    else:
                        logging.info(f"A file with the name {newFileName} already exists. Skipping rename for {os.path.basename(filepath)}. DUPLICATE_DIR not set.")
                        log_debug("DUPLICATE_DIR not set, skipping duplicate move", filepath=filepath)
                else:
                    logging.info(f"Renaming file to: {newFileName}")
                    log_debug("Attempting to rename file", old=filepath, new=newFilePath)
                    
                    try:
                        os.rename(filepath, newFilePath)
                        final_filepath = newFilePath
                        log_debug("Successfully renamed file", new_path=final_filepath)
                        # Record the rename in file store
                        record_file_change('rename', old_path=filepath, new_path=newFilePath)
                    except Exception as e:
                        log_error_with_context(
                            e,
                            context=f"Renaming file: {filepath} to {newFilePath}",
                            additional_info={"old_path": filepath, "new_path": newFilePath}
                        )
                        logging.info(f"Error renaming file {os.path.basename(filepath)}: {e}")
            else:
                logging.info(f"Filename already correct for {os.path.basename(filepath)}, skipping rename.")
                log_debug("Filename already correct, no rename needed", filepath=filepath)
        except Exception as e:
            log_error_with_context(
                e,
                context=f"Processing filename for file: {filepath}",
                additional_info={"filepath": filepath, "fixfilename": fixfilename}
            )
            logging.info(f"Could not format filename for {os.path.basename(filepath)}. Skipping rename... {e}")
    
    # Record processing history if any changes were made
    try:
        ca_final = ComicArchive(final_filepath)
        tags_final = ca_final.read_tags('cr')
        
        # Capture "after" state
        after_filename = os.path.basename(final_filepath)
        after_title = tags_final.title if tags_final else None
        after_series = tags_final.series if tags_final else None
        after_issue = tags_final.issue if tags_final else None
        after_publisher = tags_final.publisher if tags_final else None
        after_year = str(tags_final.year) if tags_final and tags_final.year else None
        after_volume = str(tags_final.volume) if tags_final and tags_final.volume else None
        
        # Only record if something changed
        if (before_filename != after_filename or 
            before_title != after_title or 
            before_series != after_series or 
            before_issue != after_issue or
            before_publisher != after_publisher or
            before_year != after_year or
            before_volume != after_volume):
            
            add_processing_history(
                filepath=final_filepath,
                operation_type='process',
                before_filename=before_filename,
                after_filename=after_filename,
                before_title=before_title,
                after_title=after_title,
                before_series=before_series,
                after_series=after_series,
                before_issue=before_issue,
                after_issue=after_issue,
                before_publisher=before_publisher,
                after_publisher=after_publisher,
                before_year=before_year,
                after_year=after_year,
                before_volume=before_volume,
                after_volume=after_volume
            )
            log_debug("Processing history recorded", filepath=final_filepath)
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Recording processing history for: {final_filepath}",
            additional_info={"filepath": final_filepath}
        )
        logging.warning(f"Could not record processing history: {e}")
    
    log_debug("File processing complete", final_path=final_filepath)
    log_function_exit("process_file", result=final_filepath)
    return final_filepath

if __name__ == "__main__":
    log_debug("process_file script started", args=sys.argv)
    
    if len(sys.argv) < 2:
        print("Usage: process_file.py <filepath>")
        log_debug("Insufficient arguments provided")
        sys.exit(1)
    
    fixtitle = '--fixtitle' in sys.argv
    fixseries = '--fixseries' in sys.argv
    fixfilename = '--fixfilename' in sys.argv
    comicfolder = None
    
    for arg in sys.argv[2:]:
        if arg.startswith('--comicfolder='):
            comicfolder = arg.split('=', 1)[1]
    
    original_filepath = sys.argv[1]
    log_debug("Processing file from command line", filepath=original_filepath, fixtitle=fixtitle, fixseries=fixseries, fixfilename=fixfilename)
    
    try:
        final_filepath = process_file(original_filepath, fixtitle=fixtitle or True, fixseries=fixseries or True, fixfilename=fixfilename or True, comicfolder=comicfolder)
        
        # Mark as processed using the final filepath (after any rename)
        log_debug("Marking file as processed", final_path=final_filepath, original_path=original_filepath)
        mark_file_processed(final_filepath, original_filepath=original_filepath)
        
        log_debug("process_file script completed successfully", final_filepath=final_filepath)
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Running process_file script on: {original_filepath}",
            additional_info={"filepath": original_filepath, "args": sys.argv}
        )
        raise