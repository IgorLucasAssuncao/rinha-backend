CREATE TABLE payments (
  CorrelationId CHAR(36) PRIMARY KEY,
  Amount DECIMAL(15,2) NOT NULL,
  IsDefault TINYINT NOT NULL,
  RequestedAt TIMESTAMP NOT NULL
) 

CREATE INDEX idx_requested_at_processor ON payments(RequestedAt, IsDefault);