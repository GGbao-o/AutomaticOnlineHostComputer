# Resume Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 生成一份面向 C# 上位机岗位的 A4 两页单栏简历，并同时输出可编辑 DOCX 与投递用 PDF 到桌面。

**Architecture:** 先建立一份结构化简历内容模型，再由同一个 Python 构建脚本分别写入 DOCX 和 PDF，避免两种格式内容不一致。DOCX 使用 `python-docx`，PDF 使用 ReportLab；PDF 渲染为 PNG 后逐页检查，DOCX进行结构、分页、字体和内容一致性检查。

**Tech Stack:** Bundled Python, python-docx, ReportLab, pypdf, pdfplumber, Poppler, OOXML

---

### Task 1: Prepare source content and output structure

**Files:**
- Create: `tmp/resume_build/build_resume.py`
- Create: `output/resume/卜松浩_CSharp上位机工程师_优化版.docx`
- Create: `output/resume/卜松浩_CSharp上位机工程师_优化版.pdf`

- [ ] **Step 1: Load the source resume and confirmed design**

Read `C:\Users\30935\Desktop\卜松浩简历.docx` and `Docs/superpowers/specs/2026-09-17-resume-redesign-design.md`.

- [ ] **Step 2: Define one structured content model**

Create Python dictionaries for identity, summary, education, skill groups, two internships, project bullets, certificates and honors. Both output formats must consume the same data.

- [ ] **Step 3: Apply content boundaries**

Use “实习生/参与/协同完成” language; remove the 75% production comparison and Redis/Linux self-demotion; retain 22-hour operation only as a neutral project fact.

### Task 2: Build the DOCX

**Files:**
- Create: `tmp/resume_build/build_resume.py`
- Create: `output/resume/卜松浩_CSharp上位机工程师_优化版.docx`

- [ ] **Step 1: Mark the DOCX artifact operation**

Run the bundled `mark_artifact_operation_started.mjs` with `--operation-kind edit --expected-output-count 1 --output-format docx` exactly once.

- [ ] **Step 2: Configure A4 document geometry**

Use portrait A4, approximately 1.6 cm horizontal margins and 1.4 cm vertical margins. Set the default Chinese font to Microsoft YaHei or a compatible CJK sans serif.

- [ ] **Step 3: Build the two-page single-column layout**

Page 1 contains header, summary, education, skills and Qingdao internship. Insert an explicit page break. Page 2 contains Dongguan internship, certificates and honors.

- [ ] **Step 4: Add restrained hierarchy**

Use a 23 pt name, 12.5 pt deep-blue section headings, 10 pt body text, compact bullet spacing and no text boxes, photos, progress bars or complex sidebars.

- [ ] **Step 5: Save and structurally audit the DOCX**

Reopen with `python-docx`; verify A4 size, exactly one explicit page break, all required headings, both internships, PLC/mechanical/sensor wording, no comments, no tracked changes and no forbidden phrases.

### Task 3: Build and verify the PDF

**Files:**
- Create: `output/resume/卜松浩_CSharp上位机工程师_优化版.pdf`
- Create: `tmp/pdfs/resume_render/page-1.png`
- Create: `tmp/pdfs/resume_render/page-2.png`

- [ ] **Step 1: Mark the PDF artifact operation**

Run the bundled `mark_artifact_operation_started.mjs` with `--operation-kind create --expected-output-count 1 --output-format pdf` exactly once.

- [ ] **Step 2: Generate the PDF from the shared content model**

Use ReportLab with registered CJK fonts, matching the DOCX page structure, colors, margins and typography.

- [ ] **Step 3: Reopen and inspect PDF metadata and text**

Use `pypdf` and `pdfplumber` to require two pages, extractable Chinese text, both company names, PLC/mechanical/sensor language and no forbidden phrases.

- [ ] **Step 4: Render every PDF page to PNG**

Use bundled Poppler `pdftoppm -png` and inspect both pages at high detail for clipping, overlap, font substitution, broken bullets and poor page balance.

- [ ] **Step 5: Iterate if any visual issue is found**

Adjust spacing or font sizes in the shared builder, regenerate both formats and repeat the structural and visual checks.

### Task 4: Deliver both artifacts to the desktop

**Files:**
- Copy: `output/resume/卜松浩_CSharp上位机工程师_优化版.docx` to `C:\Users\30935\Desktop\卜松浩_CSharp上位机工程师_优化版.docx`
- Copy: `output/resume/卜松浩_CSharp上位机工程师_优化版.pdf` to `C:\Users\30935\Desktop\卜松浩_CSharp上位机工程师_优化版.pdf`

- [ ] **Step 1: Verify output hashes and sizes**

Require both workspace files to exist and be non-empty; record SHA-256 hashes.

- [ ] **Step 2: Copy without overwriting the source resume**

Copy only the two new filenames to the desktop. Do not change `卜松浩简历.docx`.

- [ ] **Step 3: Verify desktop copies**

Compare desktop hashes against workspace hashes and confirm the original source hash is unchanged.

- [ ] **Step 4: Clean temporary render files**

Remove `tmp/resume_build` and `tmp/pdfs/resume_render`; retain only final workspace outputs and desktop deliverables.
