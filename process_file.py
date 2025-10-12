import sys
import os
import re
import logging
from comicapi.comicarchive import ComicArchive

# Set up logging to file and stdout (same as watcher.py)
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s %(message)s',
    handlers=[
        logging.FileHandler("ComicMaintainer.log"),
        logging.StreamHandler(sys.stdout)
    ]
)

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

def process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True, comicfolder=None):
    logging.info(f"Processing file: {filepath}")
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

    # Filename logic
    if fixfilename:
        if not comicfolder:
            comicfolder = os.path.dirname(filepath)
        seriesname = os.path.basename(comicfolder)
        seriesname = re.sub(r"\(\*\)|\[\*\]", "", seriesname).strip()
        num = issue_number
        if seriesname and num:
            try:
                issue_float = float(num)
                integer = int(issue_float)
                decimal = round((issue_float - integer) * 100)
                issueNumberFormatted = f"{integer:04d}"
                if decimal:
                    newFileName = f"{seriesname} - Chapter {issueNumberFormatted}.{decimal}.cbz"
                else:
                    newFileName = f"{seriesname} - Chapter {issueNumberFormatted}.cbz"
                newFilePath = os.path.join(os.path.dirname(filepath), newFileName)
                if os.path.abspath(filepath) != os.path.abspath(newFilePath):
                    if os.path.exists(newFilePath):
                        logging.info(f"A file with the name {newFileName} already exists. Skipping rename for {os.path.basename(filepath)}.")
                    else:
                        logging.info(f"Renaming file to: {newFileName}")
                        try:
                            os.rename(filepath, newFilePath)
                        except Exception as e:
                            logging.info(f"Error renaming file {os.path.basename(filepath)}: {e}")
                else:
                    logging.info(f"Filename already correct for {os.path.basename(filepath)}, skipping rename.")
            except Exception as e:
                logging.info(f"Could not extract series or issue number for {os.path.basename(filepath)}. Skipping rename... {e}")
        else:
            logging.info(f"Could not extract series or issue number for {os.path.basename(filepath)}. Skipping rename...")

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
    process_file(sys.argv[1], fixtitle=fixtitle or True, fixseries=fixseries or True, fixfilename=fixfilename or True, comicfolder=comicfolder)