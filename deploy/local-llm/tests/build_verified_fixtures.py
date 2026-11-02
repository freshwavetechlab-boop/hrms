"""Synthetic golden professional patterns. No candidate personal information."""
import hashlib
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from training_core import validated_example


def fixtures():
    companies = ['Example Research Labs', 'Sample Cloud Systems', 'Demo Transport Services', 'Example Digital Works',
                 'Sample Data Platforms', 'Demo Infrastructure Labs', 'Example Security Systems', 'Sample Innovation Centre']
    titles = ['Chief Technology Officer', 'Head of Engineering', 'Principal Architect', 'Engineering Manager']
    for index in range(32):
        company = companies[index % 8] + ' Unit ' + str(index + 1)
        title = titles[index % 4]
        degree = ['MBA', 'M.Tech', 'B.Tech', 'Ph.D'][index % 4]
        current = ['Current', 'Present', 'Still working', 'Till date'][index % 4]
        layouts = [f'April 2021 - {current}\n{title}\n{company}\nBengaluru, India',
                   f'{company}\n{title}\nApril 2021 to {current}\nBengaluru, India',
                   f'Current employer: {company}\nCurrent designation: {title}\nSince April 2021 - {current}',
                   f'{title} | {company} | April 2021 - {current}']
        source = ('SKILLS\nArtificial intelligence design\nSQL\nDocker\nPROFESSIONAL EXPERIENCE\n'
            + layouts[index % 4] + '\nApril 2017 - March 2021\nSoftware Engineer\nPrevious Example Company\n'
            + f'EDUCATION\n{degree}\nExample University, 2010\nCERTIFICATIONS\nCloud Architecture Certificate\n')
        yield validated_example({'schemaVersion': 1, 'clientId': 0,
            'subjectRef': hashlib.sha256(f'FrevoGoldenProfessional-v1:{index}'.encode()).hexdigest(),
            'sourceText': source, 'reviewedFields': {'currentCompany': company, 'currentTitle': title,
                'highestQualification': degree, 'skills': ['SQL', 'Docker']}, 'verified': True,
            'verificationKind': 'verified_regression', 'verificationRef': f'GoldenProfessionalPattern-v1:{index}'})


if __name__ == '__main__':
    path = Path(__file__).with_name('verified-professional-patterns.jsonl')
    path.write_text('\n'.join(json.dumps(row, ensure_ascii=False) for row in fixtures()) + '\n', encoding='utf-8')
    print(json.dumps({'verifiedSyntheticFixtures': 32, 'file': str(path)}))
