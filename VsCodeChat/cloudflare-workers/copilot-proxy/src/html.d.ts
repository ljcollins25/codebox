// Declaration for importing HTML files as text
declare module '*.html' {
	const content: string;
	export default content;
}
