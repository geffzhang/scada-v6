-- project.app_config definition

-- Drop table

-- DROP TABLE project.app_config;

CREATE TABLE project.app_config (
	file_id int4 NOT NULL GENERATED BY DEFAULT AS IDENTITY,
	app_id int4 NOT NULL,
	"path" varchar NOT NULL,
	contents varchar NULL,
	write_time timestamp with time zone,
	CONSTRAINT un_app_config_app_path UNIQUE (app_id, path),
	CONSTRAINT pk_app_config PRIMARY KEY (file_id)
);
CREATE INDEX fki_app_config_app_fkey ON project.app_config USING btree (app_id);


-- project.app_config foreign keys

ALTER TABLE project.app_config ADD CONSTRAINT fk_app_config_app FOREIGN KEY (app_id) REFERENCES project.app(app_id);