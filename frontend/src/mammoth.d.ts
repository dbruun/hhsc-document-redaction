declare module 'mammoth/mammoth.browser' {
  interface ConvertResult {
    value: string;
    messages: unknown[];
  }
  interface ConvertOptions {
    arrayBuffer: ArrayBuffer;
  }
  export function convertToHtml(input: ConvertOptions): Promise<ConvertResult>;
  export function extractRawText(input: ConvertOptions): Promise<ConvertResult>;
  const _default: {
    convertToHtml: typeof convertToHtml;
    extractRawText: typeof extractRawText;
  };
  export default _default;
}
