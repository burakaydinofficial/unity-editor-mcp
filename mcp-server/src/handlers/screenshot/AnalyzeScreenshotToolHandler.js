import { BaseToolHandler } from '../base/BaseToolHandler.js';
import path from 'path';

/**
 * Handler for analyzing screenshots from Unity Editor
 */
export class AnalyzeScreenshotToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'analyze_screenshot',
      'Analyze Unity screenshots for content, UI elements, colors, and more',
      {
        type: 'object',
        properties: {
          imagePath: {
            type: 'string',
            description: 'Path to the screenshot file to analyze (must be within Assets folder)'
          },
          base64Data: {
            type: 'string',
            maxLength: 10485760,
            description: 'Base64 encoded image data (alternative to imagePath). Max 10 MB (base64-encoded).'
          },
          analysisType: {
            type: 'string',
            enum: ['basic', 'ui', 'content', 'full'],
            default: 'basic',
            description: 'Type of analysis: basic (colors, dimensions), ui (UI element detection), content (scene content), full (all)'
          },
          prompt: {
            type: 'string',
            description: 'Optional prompt for AI-based analysis (e.g., "Find all buttons in the UI")'
          }
        },
        required: []
      }
    );
    
    this.unityConnection = unityConnection;
  }

  /**
   * Validates the input parameters
   * @param {Object} params - The input parameters
   * @throws {Error} If validation fails
   */
  validate(params) {
    const { imagePath, base64Data, analysisType = 'basic' } = params;

    // Must provide either imagePath or base64Data
    if (!imagePath && !base64Data) {
      throw new Error('Either imagePath or base64Data must be provided');
    }

    // Cannot provide both
    if (imagePath && base64Data) {
      throw new Error('Provide either imagePath or base64Data, not both');
    }

    // Bound base64Data — Buffer.from() decodes it synchronously on the event loop; an unbounded string
    // would stall every other in-flight command. 10 MB base64 (~7.5 MB decoded) is generous. (Audit.)
    if (base64Data && base64Data.length > 10485760) {
      throw new Error('base64Data must not exceed 10 MB (base64-encoded)');
    }

    // Validate image path if provided
    if (imagePath) {
      if (!imagePath.startsWith('Assets/')) {
        throw new Error('imagePath must be within the Assets folder');
      }
      // No `..` traversal — the editor reads the path relative to the project root, so a `..` would
      // escape it and read a file outside the project (defense-in-depth with the C# side).
      if (imagePath.split(String.fromCharCode(92)).join('/').split('/').includes('..')) {
        throw new Error('imagePath must not contain ".." traversal segments');
      }

      const ext = path.extname(imagePath).toLowerCase();
      if (!['.png', '.jpg', '.jpeg'].includes(ext)) {
        throw new Error('imagePath must be a PNG or JPEG file');
      }
    }

    // Validate analysis type
    if (!['basic', 'ui', 'content', 'full'].includes(analysisType)) {
      throw new Error('analysisType must be one of: basic, ui, content, full');
    }
  }

  /**
   * Executes the screenshot analysis
   * @param {Object} params - The validated input parameters
   * @returns {Promise<Object>} The analysis result
   */
  async execute(params) {
    const { imagePath, base64Data, analysisType = 'basic', prompt } = params;

    // If we have base64 data, we can do some analysis locally
    if (base64Data) {
      return this.analyzeBase64Image(base64Data, analysisType, prompt);
    }

    // Otherwise, send to Unity for analysis
    if (!this.unityConnection.isConnected()) {
      await this.unityConnection.connect();
    }

    const response = await this.unityConnection.sendCommand('analyze_screenshot', {
      imagePath,
      analysisType
    });

    if (response.error) {
      throw new Error(response.error);
    }

    // Build comprehensive result
    const result = {
      imagePath: response.imagePath,
      width: response.width,
      height: response.height,
      format: response.format,
      fileSize: response.fileSize,
      analysisType: response.analysisType,
      message: response.message || 'Screenshot analyzed successfully'
    };

    // Add analysis results based on type
    if (response.dominantColors) {
      result.dominantColors = response.dominantColors;
    }

    if (response.uiElements) {
      result.uiElements = response.uiElements;
    }

    // A prompt requests AI/vision analysis, which is NOT implemented (only heuristic color/edge/UI analysis runs).
    // Report that honestly rather than fabricating an "AI analysis" — this fork does not emit false-success data.
    if (prompt) {
      result.aiAnalysis = {
        supported: false,
        prompt,
        reason: 'Prompt-based AI/vision analysis is not implemented. This tool performs only heuristic color/edge/UI analysis; pass the image to a vision model for semantic analysis.'
      };
    }

    return result;
  }

  /**
   * Analyzes a base64 encoded image
   * @param {string} base64Data - The base64 encoded image
   * @param {string} analysisType - The type of analysis to perform
   * @param {string} prompt - Optional AI prompt
   * @returns {Object} Analysis results
   */
  analyzeBase64Image(base64Data, analysisType, prompt) {
    // The base64 path performs NO real image analysis — only imagePath routes to the editor's heuristic color/edge/UI
    // detection. Report that honestly instead of returning fabricated per-type placeholders (no false-success data).
    const buffer = Buffer.from(base64Data, 'base64');
    return {
      source: 'base64',
      fileSize: buffer.length,
      analysisType,
      supported: false,
      message: 'Base64 image analysis is not implemented. Use imagePath for heuristic color/edge/UI analysis, or pass the base64 data to a vision model for semantic analysis.',
      ...(prompt ? { prompt } : {})
    };
  }

  /**
   * Gets example usage for this tool
   * @returns {Object} Example usage scenarios
   */
  getExamples() {
    return {
      analyzeScreenshot: {
        description: 'Analyze a screenshot file',
        params: {
          imagePath: 'Assets/Screenshots/game_view.png',
          analysisType: 'basic'
        }
      },
      analyzeUIElements: {
        description: 'Detect UI elements in screenshot',
        params: {
          imagePath: 'Assets/Screenshots/ui_capture.png',
          analysisType: 'ui'
        }
      },
      analyzeWithPrompt: {
        description: 'AI-guided analysis with prompt',
        params: {
          imagePath: 'Assets/Screenshots/scene.png',
          analysisType: 'full',
          prompt: 'Identify all interactive elements and describe the scene lighting'
        }
      },
      analyzeBase64: {
        description: 'Analyze base64 encoded image',
        params: {
          base64Data: 'iVBORw0KGgoAAAANS...',
          analysisType: 'basic'
        }
      }
    };
  }
}